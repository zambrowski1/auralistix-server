using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Speech.Synthesis;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.VisualBasic;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using NAudio.Dsp;
using NHotkey;
using NHotkey.WindowsForms;
using NAudio.CoreAudioApi;
using System.Text.Json;
using System.Runtime.InteropServices;

namespace Auralistix
{
    public partial class Form1 : Form
    {
        private const int OUT_LATENCY_MS = 150;
        private const int MIC_IN_BUF_MS = 50;
        private const int MIC_OUT_LATENCY_MS = 150;
        private const int MIC_BUFFER_MS = 1000;

        private SettingsForm settings = null!;

        // --- ДАННЫЕ ПРОФИЛЯ ---
        private AuralistixProfile currentProfile = new AuralistixProfile();
        private bool isInitializing = true;

        private readonly Dictionary<AudioFileReader, bool> activeReaders = new Dictionary<AudioFileReader, bool>();
        private readonly List<PanningSampleProvider> activePanners = new List<PanningSampleProvider>();
        private readonly List<CustomDspProvider> activeDsps = new List<CustomDspProvider>();
        private readonly List<CancellationTokenSource> activeStopTokens = new List<CancellationTokenSource>();
        private readonly List<CancellationTokenSource> activeMicStopTokens = new List<CancellationTokenSource>();

        // Для точечной остановки звуков (Hold / Toggle)
        private readonly Dictionary<string, List<CancellationTokenSource>> _soundPlaybacks = new Dictionary<string, List<CancellationTokenSource>>();

        private readonly System.Windows.Forms.Timer timer8D = new System.Windows.Forms.Timer();
        private readonly Dictionary<PanningSampleProvider, float> panDirections = new Dictionary<PanningSampleProvider, float>();
        private float panStep = 0.05f;

        private readonly System.Windows.Forms.Timer duckUiTimer = new System.Windows.Forms.Timer();
        private int duckingPeakHold = 0;

        private string currentProfilePath = "config.json";
        private SpeechSynthesizer? tts;
        private readonly List<string> tempTtsFiles = new List<string>();

        private WaveOutEvent? beepOutMonitor;
        private WaveOutEvent? beepOutMic;

        private WaveInEvent? realMicIn;
        private IWavePlayer? realMicOutCable;
        private BufferedWaveProvider? micBufferCable;
        private CustomDspProvider? micDspCable;

        private IWavePlayer? realMicOutMonitor;
        private BufferedWaveProvider? micBufferMonitor;
        private CustomDspProvider? micDspMonitor;

        private float micPeakLevel = 0f;
        private float duckingMultiplier = 1.0f;

        private AnimatedHoverTip tip = null!;
        private System.Windows.Forms.Timer tipDelayTimer = null!;
        private int lastHoverIndex = -1;
        private int pendingHoverIndex = -1;
        private string pendingText = "";
        private Point pendingScreenPos;

        private SoundItem? _bindingItem = null;

        // --- ГЛОБАЛЬНЫЕ ХОТКЕИ (API WINDOWS) ---
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(Keys vKey);

        private readonly System.Windows.Forms.Timer _bindsTimer = new System.Windows.Forms.Timer { Interval = 15 };
        private readonly Dictionary<string, bool> _keyStates = new Dictionary<string, bool>();

        // --- ДИНАМИЧЕСКИЕ ПЕРЕМЕННЫЕ (Элементы из окна SettingsForm) ---
        private CheckBox? s_cbPassthrough, s_cbDucking, s_cbNormalize, s_cbEfxSound, s_cbEfxMic, s_cbMicListen;
        private ComboBox? s_cmbVoice, s_cmbMonitor, s_cmbMicOut, s_cmbMicIn, s_cmbPreset, s_cmbEqPresets;
        private TrackBar? s_tbSens, s_tbDuckVol, s_tbNorm, s_tbLow, s_tbMid, s_tbHigh;
        private NumericUpDown? s_nudDelay;
        private Button? s_btnEqSave, s_btnEqRename, s_btnEqDelete;

        private readonly List<EqPreset> eqPresets = new List<EqPreset>();
        private bool suppressEqUi = false;
        private string eqPresetsFilePath = "";

        // --- ВСПОМОГАТЕЛЬНЫЕ КЛАССЫ ВНУТРИ ФОРМЫ ---
        private sealed class EqPreset { public string Name { get; set; } = "Preset"; public int Low { get; set; } public int Mid { get; set; } public int High { get; set; } }
        private sealed class WaveOutDeviceItem { public int DeviceNumber { get; } public string Name { get; } public WaveOutDeviceItem(int d, string n) { DeviceNumber = d; Name = n; } public override string ToString() => Name; }
        private sealed class WaveInDeviceItem { public int DeviceNumber { get; } public string Name { get; } public WaveInDeviceItem(int d, string n) { DeviceNumber = d; Name = n; } public override string ToString() => Name; }

        public Form1()
        {
            InitializeComponent();
            if (LicenseManager.UsageMode == LicenseUsageMode.Designtime) return;

            settings = new SettingsForm();
            eqPresetsFilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Auralistix", "eq_presets.txt");

            BindControls();
            SetupListView();
            SetupTreeViewDragAndDrop();

            tip = new AnimatedHoverTip();
            tipDelayTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            tipDelayTimer.Tick += TipDelayTimer_Tick;

            SetupManualEvents();
            LoadWaveOutDevices();
            LoadWaveInDevices();
            InitEffectsUI();
            InitEqPresetsUI();
            LoadEqPresets();
            InitTts();
            LoadPlaylist(currentProfilePath);

            this.Load += (s, e) =>
            {
                timer8D.Interval = 50; timer8D.Tick += Timer8D_Tick; timer8D.Start();
                duckUiTimer.Interval = 30; duckUiTimer.Tick += DuckUiTimer_Tick; duckUiTimer.Start();
                _bindsTimer.Tick += BindsTimer_Tick; _bindsTimer.Start();
            };

            this.Shown += (sender, args) =>
            {
                isInitializing = false;
                try { if (IsMicRoutingNeeded()) UpdateMicRouting(); } catch { }
            };
        }

        private T? GetSafe<T>(string name) where T : Control
        {
            var ctrl = this.Controls.Find(name, true).FirstOrDefault() as T;
            if (ctrl != null) return ctrl;
            if (settings != null) return settings.Controls.Find(name, true).FirstOrDefault() as T;
            return null;
        }

        private void BindControls()
        {
            s_cbPassthrough = GetSafe<CheckBox>("checkBox6"); s_cbDucking = GetSafe<CheckBox>("checkBox7"); s_cbNormalize = GetSafe<CheckBox>("checkBox8");
            s_cbEfxSound = GetSafe<CheckBox>("checkBox9"); s_cbEfxMic = GetSafe<CheckBox>("checkBox10"); s_cbMicListen = GetSafe<CheckBox>("checkBox11");
            s_cmbVoice = GetSafe<ComboBox>("comboBox1"); s_cmbMonitor = GetSafe<ComboBox>("comboBox2"); s_cmbMicOut = GetSafe<ComboBox>("comboBox3");
            s_cmbMicIn = GetSafe<ComboBox>("comboBox4"); s_cmbPreset = GetSafe<ComboBox>("comboBox5"); s_cmbEqPresets = GetSafe<ComboBox>("comboBox6");
            s_btnEqSave = GetSafe<Button>("button6"); s_btnEqRename = GetSafe<Button>("button7"); s_btnEqDelete = GetSafe<Button>("button8");
            s_tbSens = GetSafe<TrackBar>("trackBar2"); s_tbDuckVol = GetSafe<TrackBar>("trackBar3"); s_tbNorm = GetSafe<TrackBar>("trackBar4");
            s_tbLow = GetSafe<TrackBar>("trackBar5"); s_tbMid = GetSafe<TrackBar>("trackBar6"); s_tbHigh = GetSafe<TrackBar>("trackBar7");
            s_nudDelay = GetSafe<NumericUpDown>("numericUpDown1");

            treeViewBanks.HideSelection = false;
            treeViewBanks.AfterSelect += (s, e) => RefreshListView();
            txtSearch.TextChanged += (s, e) => RefreshListView();
            btnAddBank.Click += BtnAddBank_Click;
            btnDelBank.Click += BtnDelBank_Click;

            trackBar1.Scroll += trackBar1_Scroll;
            trackBarMic.Scroll += trackBar1_Scroll;

            if (listView1 != null && contextMenuStrip1 != null)
            {
                var setBindMenu = new ToolStripMenuItem("Назначить бинд", null, (s, e) => {
                    var selected = GetSelectedSound();
                    if (selected != null && listView1.SelectedItems.Count > 0)
                    {
                        _bindingItem = selected;
                        listView1.SelectedItems[0].SubItems[3].Text = "[Нажмите клавишу...]";
                    }
                });
                contextMenuStrip1.Items.Insert(1, setBindMenu);

                var modeMenu = new ToolStripMenuItem("Режим воспроизведения");
                var modeNormal = new ToolStripMenuItem("Обычный", null, (s, e) => SetPlayMode(PlaybackMode.Normal));
                var modeHold = new ToolStripMenuItem("Удержание (Hold-to-play)", null, (s, e) => SetPlayMode(PlaybackMode.HoldToPlay));
                var modeToggle = new ToolStripMenuItem("Переключатель (Toggle)", null, (s, e) => SetPlayMode(PlaybackMode.Toggle));

                modeMenu.DropDownItems.Add(modeNormal);
                modeMenu.DropDownItems.Add(modeHold);
                modeMenu.DropDownItems.Add(modeToggle);

                contextMenuStrip1.Items.Add(new ToolStripSeparator());
                contextMenuStrip1.Items.Add(modeMenu);

                contextMenuStrip1.Opening += (s, e) => {
                    var selected = GetSelectedSound();
                    if (selected != null)
                    {
                        modeNormal.Checked = (selected.PlayMode == PlaybackMode.Normal);
                        modeHold.Checked = (selected.PlayMode == PlaybackMode.HoldToPlay);
                        modeToggle.Checked = (selected.PlayMode == PlaybackMode.Toggle);
                    }
                };

                listView1.ContextMenuStrip = contextMenuStrip1;
            }
        }

        private void SetPlayMode(PlaybackMode mode)
        {
            if (listView1.SelectedItems.Count == 0) return;
            var soundsToChange = listView1.SelectedItems.Cast<ListViewItem>().Select(i => i.Tag as SoundItem).Where(s => s != null).ToList();
            foreach (var s in soundsToChange) s!.PlayMode = mode;
            AutoSaveProfile();
            RefreshListView();
        }

        private void SetupListView()
        {
            listView1.View = View.Details;
            listView1.FullRowSelect = true;
            listView1.AllowDrop = true;
            listView1.GridLines = true;

            // Включаем мультивыбор (Shift, Ctrl)
            listView1.MultiSelect = true;

            listView1.Columns.Add("№", 40);
            listView1.Columns.Add("Имя файла", 200);
            listView1.Columns.Add("Длительность", 90);
            listView1.Columns.Add("Бинд", 120);

            listView1.DragEnter += (s, e) => { if (e.Data!.GetDataPresent(typeof(List<ListViewItem>)) || e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy; };
            listView1.DragDrop += (s, e) =>
            {
                if (e.Data!.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] paths)
                {
                    foreach (var path in paths)
                    {
                        if (Directory.Exists(path)) { foreach (var f in Directory.GetFiles(path, "*.*", SearchOption.AllDirectories)) AddFileToPlaylist(f); }
                        else if (File.Exists(path)) AddFileToPlaylist(path);
                    }
                    RefreshListView(); AutoSaveProfile();
                }
            };

            listView1.ItemDrag += (s, e) => {
                var draggedItems = listView1.SelectedItems.Cast<ListViewItem>().ToList();
                listView1.DoDragDrop(draggedItems, DragDropEffects.Move);
            };
        }

        private bool IsMicRoutingNeeded() => (s_cbPassthrough?.Checked ?? false) || (s_cbMicListen?.Checked ?? false);
        private bool CanSendSoundToMic() => s_cbPassthrough?.Checked ?? false;

        private List<SoundItem> GetAllSounds()
        {
            var allSounds = new List<SoundItem>();
            foreach (var bank in currentProfile.Banks) allSounds.AddRange(bank.Sounds);
            return allSounds;
        }

        private SoundItem? GetSelectedSound()
        {
            if (listView1.SelectedItems.Count > 0) return listView1.SelectedItems[0].Tag as SoundItem;
            return null;
        }

        // --- ГЛОБАЛЬНАЯ ЛОГИКА ГОРЯЧИХ КЛАВИШ ---
        private bool IsKeyDown(Keys key) => (GetAsyncKeyState(key) & 0x8000) != 0;

        private bool IsBindPressed(Keys bind)
        {
            Keys mainKey = bind & ~Keys.Control & ~Keys.Alt & ~Keys.Shift;
            if (mainKey == Keys.None) return false;

            bool needCtrl = (bind & Keys.Control) != 0;
            bool needAlt = (bind & Keys.Alt) != 0;
            bool needShift = (bind & Keys.Shift) != 0;

            if (needCtrl && !IsKeyDown(Keys.ControlKey) && !IsKeyDown(Keys.LControlKey) && !IsKeyDown(Keys.RControlKey)) return false;
            if (needAlt && !IsKeyDown(Keys.Menu) && !IsKeyDown(Keys.LMenu) && !IsKeyDown(Keys.RMenu)) return false;
            if (needShift && !IsKeyDown(Keys.ShiftKey) && !IsKeyDown(Keys.LShiftKey) && !IsKeyDown(Keys.RShiftKey)) return false;

            return IsKeyDown(mainKey);
        }

        private void BindsTimer_Tick(object? sender, EventArgs e)
        {
            if (isInitializing || _bindingItem != null) return;

            foreach (var sound in GetAllSounds())
            {
                if (string.IsNullOrWhiteSpace(sound.BindKey)) continue;

                if (Enum.TryParse(sound.BindKey, out Keys bindCode))
                {
                    bool isPressed = IsBindPressed(bindCode);
                    bool wasPressed = _keyStates.TryGetValue(sound.Id, out bool val) && val;

                    if (isPressed && !wasPressed) HandleKeyDown(sound);
                    else if (!isPressed && wasPressed) HandleKeyUp(sound);

                    _keyStates[sound.Id] = isPressed;
                }
            }
        }

        private void HandleKeyDown(SoundItem sound)
        {
            if (sound.PlayMode == PlaybackMode.Toggle)
            {
                if (IsSoundPlaying(sound)) StopSpecificSound(sound);
                else PlaySoundItem(sound);
            }
            else PlaySoundItem(sound);
        }

        private void HandleKeyUp(SoundItem sound)
        {
            if (sound.PlayMode == PlaybackMode.HoldToPlay) StopSpecificSound(sound);
        }

        private bool IsSoundPlaying(SoundItem sound)
        {
            lock (_soundPlaybacks) return _soundPlaybacks.ContainsKey(sound.Id) && _soundPlaybacks[sound.Id].Count > 0;
        }

        private void StopSpecificSound(SoundItem sound)
        {
            lock (_soundPlaybacks)
            {
                if (_soundPlaybacks.TryGetValue(sound.Id, out var tokens))
                {
                    foreach (var cts in tokens.ToList()) try { cts.Cancel(); } catch { }
                    tokens.Clear();
                }
            }
        }

        private void SetupManualEvents()
        {
            // listView (ховер-подсказка, бинды, двойной клик)
            if (listView1 != null)
            {
                listView1.MouseMove -= ListView1_MouseMove;
                listView1.MouseMove += ListView1_MouseMove;

                listView1.MouseLeave -= ListView1_MouseLeave;
                listView1.MouseLeave += ListView1_MouseLeave;

                listView1.MouseDoubleClick -= ListView1_MouseDoubleClick;
                listView1.MouseDoubleClick += ListView1_MouseDoubleClick;

                listView1.MouseClick -= ListView1_MouseClick;
                listView1.MouseClick += ListView1_MouseClick;

                listView1.KeyDown -= ListView1_KeyDown;
                listView1.KeyDown += ListView1_KeyDown;
            }

            // seek по прогресс-бару
            if (progressBar1 != null)
            {
                progressBar1.MouseDown -= progressBar1_MouseDown;
                progressBar1.MouseDown += progressBar1_MouseDown;
            }

            // TTS кнопка
            if (button4 != null)
            {
                button4.Click -= button4_Click;
                button4.Click += button4_Click;
            }

            // Настройки
            if (menuSettingsBtn != null)
            {
                menuSettingsBtn.Click -= MenuSettingsBtn_Click;
                menuSettingsBtn.Click += MenuSettingsBtn_Click;
            }

            // Пункты контекстного меню файлов
            if (воспроизвестиToolStripMenuItem != null)
            {
                воспроизвестиToolStripMenuItem.Click -= PlayMenuItem_Click;
                воспроизвестиToolStripMenuItem.Click += PlayMenuItem_Click;
            }

            if (переименоватьToolStripMenuItem != null)
            {
                переименоватьToolStripMenuItem.Click -= RenameMenuItem_Click;
                переименоватьToolStripMenuItem.Click += RenameMenuItem_Click;
            }

            if (удалитьToolStripMenuItem != null)
            {
                удалитьToolStripMenuItem.Click -= DeleteMenuItem_Click;
                удалитьToolStripMenuItem.Click += DeleteMenuItem_Click;
            }

            if (красныйToolStripMenuItem != null)
            {
                красныйToolStripMenuItem.Click -= RedCategoryMenuItem_Click;
                красныйToolStripMenuItem.Click += RedCategoryMenuItem_Click;
            }

            if (жёлтыйToolStripMenuItem != null)
            {
                жёлтыйToolStripMenuItem.Click -= YellowCategoryMenuItem_Click;
                жёлтыйToolStripMenuItem.Click += YellowCategoryMenuItem_Click;
            }

            if (синийToolStripMenuItem != null)
            {
                синийToolStripMenuItem.Click -= BlueCategoryMenuItem_Click;
                синийToolStripMenuItem.Click += BlueCategoryMenuItem_Click;
            }

            // Beep кнопка (hold)
            if (button5 != null)
            {
                button5.MouseDown -= button5_MouseDown;
                button5.MouseDown += button5_MouseDown;

                button5.MouseUp -= button5_MouseUp;
                button5.MouseUp += button5_MouseUp;
            }

            // Переключатели/устройства микрофона — чтобы сразу перестраивать роутинг
            if (s_cbPassthrough != null)
            {
                s_cbPassthrough.CheckedChanged -= (s, e) => UpdateMicRouting();
                s_cbPassthrough.CheckedChanged += (s, e) => UpdateMicRouting();
            }

            if (s_cbMicListen != null)
            {
                s_cbMicListen.CheckedChanged -= (s, e) => UpdateMicRouting();
                s_cbMicListen.CheckedChanged += (s, e) => UpdateMicRouting();
            }

            if (s_cmbMicIn != null)
            {
                s_cmbMicIn.SelectedIndexChanged -= (s, e) => UpdateMicRouting();
                s_cmbMicIn.SelectedIndexChanged += (s, e) => UpdateMicRouting();
            }

            if (s_cmbMicOut != null)
            {
                s_cmbMicOut.SelectedIndexChanged -= (s, e) => UpdateMicRouting();
                s_cmbMicOut.SelectedIndexChanged += (s, e) => UpdateMicRouting();
            }

            if (s_cmbMonitor != null)
            {
                s_cmbMonitor.SelectedIndexChanged -= (s, e) => UpdateMicRouting();
                s_cmbMonitor.SelectedIndexChanged += (s, e) => UpdateMicRouting();
            }

            // (опционально) хоткей стопа через NHotkey, если хочешь:
            // try { HotkeyManager.Current.AddOrReplace("StopAll", Keys.F8, OnStopHotkeyPressed); } catch { }
        }

        // --- ВОСПРОИЗВЕДЕНИЕ ---
        private async void PlaySoundItem(SoundItem sound)
        {
            if (!File.Exists(sound.Path)) return;

            if (!checkBox2.Checked) StopAllSounds();

            bool sendToMonitor = checkBox4.Checked;
            bool sendToMic = CanSendSoundToMic();

            if (!sendToMonitor && !sendToMic) return;

            int monitorDevice = GetSelectedDeviceNumber(s_cmbMonitor);
            int micDevice = GetSelectedDeviceNumber(s_cmbMicOut);

            var tasks = new List<Task>();
            var createdTokens = new List<CancellationTokenSource>();

            lock (_soundPlaybacks)
            {
                if (!_soundPlaybacks.ContainsKey(sound.Id)) _soundPlaybacks[sound.Id] = new List<CancellationTokenSource>();
            }

            if (sendToMonitor)
            {
                var cts = new CancellationTokenSource();
                createdTokens.Add(cts);
                lock (activeStopTokens) activeStopTokens.Add(cts);
                lock (_soundPlaybacks) _soundPlaybacks[sound.Id].Add(cts);
                tasks.Add(PlayToDeviceAsync(sound.Path, monitorDevice, cts.Token, false, sound));
            }

            if (sendToMic)
            {
                var cts = new CancellationTokenSource();
                createdTokens.Add(cts);
                lock (activeStopTokens) activeStopTokens.Add(cts);
                lock (activeMicStopTokens) activeMicStopTokens.Add(cts);
                lock (_soundPlaybacks) _soundPlaybacks[sound.Id].Add(cts);
                tasks.Add(PlayToDeviceAsync(sound.Path, micDevice, cts.Token, true, sound));
            }

            try { await Task.WhenAll(tasks); }
            catch { }
            finally
            {
                foreach (var cts in createdTokens)
                {
                    lock (activeStopTokens) activeStopTokens.Remove(cts);
                    lock (activeMicStopTokens) activeMicStopTokens.Remove(cts);

                    // Безопасное удаление из словаря (ИСПРАВЛЕН КРАШ)
                    lock (_soundPlaybacks)
                    {
                        if (_soundPlaybacks.TryGetValue(sound.Id, out var tokens))
                        {
                            tokens.Remove(cts);
                            if (tokens.Count == 0) _soundPlaybacks.Remove(sound.Id);
                        }
                    }
                    try { cts.Dispose(); } catch { }
                }
            }
        }

        private async Task PlayToDeviceAsync(string audioPath, int deviceNumber, CancellationToken token, bool isMicChannel, SoundItem currentSound)
        {
            var reader = new AudioFileReader(audioPath);
            ISampleProvider source = EnsureMono(reader.ToSampleProvider());

            var dspProvider = new CustomDspProvider(source)
            {
                EnableNormalize = s_cbNormalize?.Checked ?? false,
                EffectPreset = s_cmbPreset?.SelectedItem?.ToString() ?? "Нет",
                NormalizeGain = (s_tbNorm?.Value ?? 25) / 10f,
                Bypass = !(s_cbEfxSound?.Checked ?? true)
            };

            dspProvider.UpdateEq(s_tbLow?.Value ?? 0, s_tbMid?.Value ?? 0, s_tbHigh?.Value ?? 0);
            var panner = new PanningSampleProvider(dspProvider) { Pan = 0f };

            lock (activeReaders) activeReaders[reader] = isMicChannel;
            lock (activePanners) { activePanners.Add(panner); panDirections[panner] = panStep; }
            lock (activeDsps) activeDsps.Add(dspProvider);

            try
            {
                while (!token.IsCancellationRequested)
                {
                    IWavePlayer? output = null;
                    try
                    {
                        output = CreatePlayerForSound(deviceNumber, panner.ToWaveProvider16());
                        reader.Volume = (isMicChannel ? trackBarMic.Value : trackBar1.Value) / 100f * duckingMultiplier;
                        output.Play();
                        while (output.PlaybackState == PlaybackState.Playing) { if (token.IsCancellationRequested) { output.Stop(); break; } await Task.Delay(30); }
                    }
                    finally { try { output?.Stop(); output?.Dispose(); } catch { } }

                    bool shouldLoop = checkBox1.Checked || (currentSound.PlayMode == PlaybackMode.Toggle);
                    if (!shouldLoop || token.IsCancellationRequested) break;

                    int delay = (int)(s_nudDelay?.Value ?? 0);
                    if (delay > 0) await Task.Delay(delay, token);

                    reader.Position = 0;
                }
            }
            finally
            {
                lock (activeReaders) activeReaders.Remove(reader);
                lock (activePanners) { activePanners.Remove(panner); panDirections.Remove(panner); }
                lock (activeDsps) activeDsps.Remove(dspProvider);
                reader.Dispose();
            }
        }

        // --- ЛОГИКА ПАПОК И ДЕРЕВА ---
        private SoundBank? GetSelectedBank()
        {
            if (treeViewBanks.SelectedNode?.Tag is SoundBank bank) return bank;
            return currentProfile.Banks.FirstOrDefault();
        }

        private void RefreshTreeView()
        {
            string selectedId = GetSelectedBank()?.Id ?? "";
            treeViewBanks.Nodes.Clear();
            TreeNode? nodeToSelect = null;

            foreach (var bank in currentProfile.Banks)
            {
                var node = new TreeNode(bank.Name) { Tag = bank };
                treeViewBanks.Nodes.Add(node);
                if (bank.Id == selectedId) nodeToSelect = node;
            }
            if (nodeToSelect != null) treeViewBanks.SelectedNode = nodeToSelect;
            else if (treeViewBanks.Nodes.Count > 0) treeViewBanks.SelectedNode = treeViewBanks.Nodes[0];
        }

        private void SetupTreeViewDragAndDrop()
        {
            treeViewBanks.AllowDrop = true;
            treeViewBanks.DragEnter += (s, e) => {
                if (e.Data!.GetDataPresent(typeof(List<ListViewItem>))) e.Effect = DragDropEffects.Move;
            };

            treeViewBanks.DragDrop += (s, e) =>
            {
                if (e.Data!.GetDataPresent(typeof(List<ListViewItem>)))
                {
                    var items = (List<ListViewItem>)e.Data.GetData(typeof(List<ListViewItem>))!;
                    Point targetPoint = treeViewBanks.PointToClient(new Point(e.X, e.Y));
                    TreeNode? targetNode = treeViewBanks.GetNodeAt(targetPoint);

                    if (targetNode != null && targetNode.Tag is SoundBank targetBank)
                    {
                        var sourceBank = GetSelectedBank();
                        if (sourceBank != null && sourceBank.Id != targetBank.Id)
                        {
                            foreach (var lvi in items)
                            {
                                if (lvi.Tag is SoundItem sound)
                                {
                                    sourceBank.Sounds.Remove(sound);
                                    targetBank.Sounds.Add(sound);
                                }
                            }
                            RefreshListView(); AutoSaveProfile();
                        }
                    }
                }
            };
        }

        private void BtnAddBank_Click(object? sender, EventArgs e)
        {
            string name = Interaction.InputBox("Имя новой папки:", "Auralistix", "Новый банк");
            if (!string.IsNullOrWhiteSpace(name)) { currentProfile.Banks.Add(new SoundBank { Name = name.Trim() }); RefreshTreeView(); AutoSaveProfile(); }
        }

        private void BtnDelBank_Click(object? sender, EventArgs e)
        {
            var bank = GetSelectedBank();
            if (bank != null)
            {
                if (MessageBox.Show($"Удалить папку '{bank.Name}'?", "Удаление", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                {
                    currentProfile.Banks.Remove(bank); RefreshTreeView(); RefreshListView(); AutoSaveProfile();
                }
            }
        }

        private void AddFileToPlaylist(string path)
        {
            if (path.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
            {
                var item = new SoundItem { Name = Path.GetFileName(path), Path = path, BindKey = "" };
                try { using var r = new AudioFileReader(path); item.Duration = r.TotalTime.ToString(@"mm\:ss"); } catch { item.Duration = "00:00"; }
                if (currentProfile.Banks.Count == 0) currentProfile.Banks.Add(new SoundBank { Name = "Основной банк" });
                var targetBank = GetSelectedBank() ?? currentProfile.Banks[0];
                targetBank.Sounds.Add(item);
            }
        }

        private void RefreshListView()
        {
            var searchQuery = txtSearch.Text.Trim().ToLowerInvariant();
            IEnumerable<SoundItem> soundsToDisplay;

            if (!string.IsNullOrEmpty(searchQuery))
                soundsToDisplay = GetAllSounds().Where(s => s.Name.ToLowerInvariant().Contains(searchQuery) || s.BindKey.ToLowerInvariant().Contains(searchQuery));
            else
            {
                var bank = GetSelectedBank();
                soundsToDisplay = bank != null ? bank.Sounds : Enumerable.Empty<SoundItem>();
            }

            soundsToDisplay = soundsToDisplay.OrderBy(s => s.Category == "Red" ? 0 : s.Category == "Yellow" ? 1 : s.Category == "Blue" ? 2 : 3).ThenBy(s => s.Name);

            listView1.BeginUpdate();
            listView1.Items.Clear();
            int index = 1;
            foreach (var item in soundsToDisplay)
            {
                var lvi = new ListViewItem(index.ToString());
                lvi.SubItems.Add(item.Name);
                lvi.SubItems.Add(item.Duration);

                string bindText = string.IsNullOrWhiteSpace(item.BindKey) ? "Нет" : item.BindKey.Replace(", ", " + ");
                if (item.PlayMode == PlaybackMode.HoldToPlay) bindText += " (Hold)";
                if (item.PlayMode == PlaybackMode.Toggle) bindText += " (Toggle)";
                lvi.SubItems.Add(bindText);

                if (item.Category == "Red") lvi.ForeColor = Color.IndianRed;
                else if (item.Category == "Yellow") lvi.ForeColor = Color.Goldenrod;
                else if (item.Category == "Blue") lvi.ForeColor = Color.DodgerBlue;

                lvi.Tag = item;
                listView1.Items.Add(lvi);
                index++;
            }
            listView1.EndUpdate();
        }

        // --- СОБЫТИЯ МЫШИ ДЛЯ СПИСКА И ПОДСКАЗОК ---
        private void ListView1_MouseMove(object? sender, MouseEventArgs e)
        {
            var item = listView1.GetItemAt(e.X, e.Y);
            var screenPos = listView1.PointToScreen(new Point(e.X + 20, e.Y + 20));

            if (item == null) { tipDelayTimer.Stop(); pendingHoverIndex = -1; lastHoverIndex = -1; tip.HideTip(); return; }
            if (item.Index == lastHoverIndex) { pendingScreenPos = screenPos; tip.UpdatePosition(screenPos); return; }

            lastHoverIndex = item.Index;
            tip.HideTip();
            tipDelayTimer.Stop();

            if (item.Tag is SoundItem si)
            {
                pendingHoverIndex = item.Index;
                pendingText = "Путь: " + si.Path;
                pendingScreenPos = screenPos;
                tipDelayTimer.Start();
            }
        }

        private void ListView1_MouseLeave(object? sender, EventArgs e)
        {
            tipDelayTimer.Stop();
            pendingHoverIndex = -1;
            lastHoverIndex = -1;
            tip.HideTip();
        }

        private void ListView1_MouseDoubleClick(object? sender, MouseEventArgs e)
        {
            var info = listView1.HitTest(e.X, e.Y);
            if (info.Item != null && info.Item.Tag is SoundItem item)
            {
                if (info.Item.SubItems.IndexOf(info.SubItem) == 3)
                {
                    _bindingItem = item;
                    info.SubItem.Text = "[Нажмите клавишу...]";
                }
                else PlaySoundItem(item);
            }
        }

        private void ListView1_MouseClick(object? sender, MouseEventArgs e)
        {
            if (_bindingItem != null) { _bindingItem = null; RefreshListView(); }
            if (e.Button == MouseButtons.Right)
            {
                var item = listView1.GetItemAt(e.X, e.Y);
                if (item != null && !item.Selected)
                {
                    listView1.SelectedItems.Clear();
                    item.Selected = true;
                }
            }
        }

        private void ListView1_KeyDown(object? sender, KeyEventArgs e)
        {
            if (_bindingItem != null)
            {
                e.Handled = true; e.SuppressKeyPress = true;
                if (e.KeyCode == Keys.ShiftKey || e.KeyCode == Keys.ControlKey || e.KeyCode == Keys.Menu) return;
                if (e.KeyCode == Keys.Escape) { }
                else if (e.KeyCode == Keys.Delete || e.KeyCode == Keys.Back) _bindingItem.BindKey = "";
                else _bindingItem.BindKey = e.KeyData.ToString();

                _bindingItem = null; RefreshListView(); AutoSaveProfile(); return;
            }

            // Поддержка Ctrl+A для выделения всех файлов
            if (e.Control && e.KeyCode == Keys.A)
            {
                foreach (ListViewItem item in listView1.Items) item.Selected = true;
                e.Handled = true; e.SuppressKeyPress = true;
                return;
            }

            if (e.KeyCode == Keys.Enter) { PlaySelectedSound(); e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.Delete) RemoveSelectedSound();
            else if (e.KeyCode == Keys.F2) RenameSelectedSound();
        }

        private void TipDelayTimer_Tick(object? sender, EventArgs e)
        {
            tipDelayTimer.Stop();
            if (pendingHoverIndex != -1) tip.ShowTip(pendingText, pendingScreenPos);
        }

        // --- МАССОВЫЕ ДЕЙСТВИЯ (МУЛЬТИВЫБОР) ---
        private void SetCategory(string c)
        {
            if (listView1.SelectedItems.Count == 0) return;
            var soundsToColor = listView1.SelectedItems.Cast<ListViewItem>().Select(i => i.Tag as SoundItem).Where(s => s != null).ToList();
            foreach (var s in soundsToColor) s!.Category = c;
            RefreshListView(); AutoSaveProfile();
        }

        private void RemoveSelectedSound()
        {
            if (listView1.SelectedItems.Count == 0) return;
            var soundsToRemove = listView1.SelectedItems.Cast<ListViewItem>().Select(i => i.Tag as SoundItem).Where(s => s != null).ToList();
            bool changed = false;
            foreach (var s in soundsToRemove)
            {
                foreach (var b in currentProfile.Banks)
                {
                    if (b.Sounds.Remove(s!)) { changed = true; break; }
                }
            }
            if (changed) { RefreshListView(); AutoSaveProfile(); }
        }

        private void RenameSelectedSound()
        {
            var s = GetSelectedSound();
            if (s != null)
            {
                string n = Interaction.InputBox("Имя:", "Auralistix", s.Name);
                if (!string.IsNullOrWhiteSpace(n)) { s.Name = n.Trim(); RefreshListView(); AutoSaveProfile(); }
            }
        }

        private void PlaySelectedSound() { var s = GetSelectedSound(); if (s != null) PlaySoundItem(s); }
        private void MenuSettingsBtn_Click(object? sender, EventArgs e)
        {
            if (settings.IsDisposed) settings = new SettingsForm();
            if (!settings.Visible) settings.Show(this);
            settings.BringToFront();
            settings.Activate();
        }

        private void PlayMenuItem_Click(object? sender, EventArgs e) => PlaySelectedSound();
        private void RenameMenuItem_Click(object? sender, EventArgs e) => RenameSelectedSound();
        private void DeleteMenuItem_Click(object? sender, EventArgs e) => RemoveSelectedSound();
        private void RedCategoryMenuItem_Click(object? sender, EventArgs e) => SetCategory("Red");
        private void YellowCategoryMenuItem_Click(object? sender, EventArgs e) => SetCategory("Yellow");
        private void BlueCategoryMenuItem_Click(object? sender, EventArgs e) => SetCategory("Blue");

        private void StopAllSounds() { lock (activeStopTokens) foreach (var cts in activeStopTokens.ToList()) try { cts.Cancel(); } catch { } lock (_soundPlaybacks) _soundPlaybacks.Clear(); }
        private void StopMicInjectedSounds() { lock (activeMicStopTokens) foreach (var cts in activeMicStopTokens.ToList()) try { cts.Cancel(); } catch { } StopBeep(); }
        private void OnStopHotkeyPressed(object? sender, HotkeyEventArgs e) => StopAllSounds();

        // --- ВТОРОСТЕПЕННЫЕ МЕТОДЫ (МИКРОФОН, ЭФФЕКТЫ, НАСТРОЙКИ, TTS) ---
        private void InitEffectsUI() { if (s_cmbPreset != null) { s_cmbPreset.Items.Clear(); s_cmbPreset.Items.AddRange(new[] { "Нет", "Радио (Bandpass)", "Дисторшн (Distortion)", "Эхо (Reverb)" }); s_cmbPreset.SelectedIndex = 0; s_cmbPreset.SelectedIndexChanged += (s, e) => UpdateDspSettings(); } if (s_cbNormalize != null) s_cbNormalize.CheckedChanged += (s, e) => UpdateDspSettings(); if (s_tbNorm != null) s_tbNorm.Scroll += (s, e) => UpdateDspSettings(); if (s_tbLow != null) s_tbLow.Scroll += (s, e) => UpdateDspSettings(); if (s_tbMid != null) s_tbMid.Scroll += (s, e) => UpdateDspSettings(); if (s_tbHigh != null) s_tbHigh.Scroll += (s, e) => UpdateDspSettings(); if (s_cbEfxSound != null) s_cbEfxSound.CheckedChanged += (s, e) => UpdateDspSettings(); if (s_cbEfxMic != null) s_cbEfxMic.CheckedChanged += (s, e) => UpdateDspSettings(); }
        private void UpdateDspSettings() { if (isInitializing) return; bool normalize = s_cbNormalize?.Checked ?? false; string preset = s_cmbPreset?.SelectedItem?.ToString() ?? "Нет"; float normGain = (s_tbNorm?.Value ?? 25) / 10f; float eqLow = s_tbLow?.Value ?? 0; float eqMid = s_tbMid?.Value ?? 0; float eqHigh = s_tbHigh?.Value ?? 0; bool applyToSounds = s_cbEfxSound?.Checked ?? true; bool applyToMic = s_cbEfxMic?.Checked ?? true; lock (activeDsps) { foreach (var dsp in activeDsps) { dsp.EnableNormalize = normalize; dsp.EffectPreset = preset; dsp.NormalizeGain = normGain; dsp.UpdateEq(eqLow, eqMid, eqHigh); dsp.Bypass = !applyToSounds; } } if (micDspCable != null) { micDspCable.EnableNormalize = normalize; micDspCable.EffectPreset = preset; micDspCable.NormalizeGain = normGain; micDspCable.UpdateEq(eqLow, eqMid, eqHigh); micDspCable.Bypass = !applyToMic; } if (micDspMonitor != null) { micDspMonitor.EnableNormalize = normalize; micDspMonitor.EffectPreset = preset; micDspMonitor.NormalizeGain = normGain; micDspMonitor.UpdateEq(eqLow, eqMid, eqHigh); micDspMonitor.Bypass = !applyToMic; } }
        private void UpdateMicRouting() { if (isInitializing) return; if (!IsMicRoutingNeeded()) StopMicPassthrough(); else StartMicPassthrough(); }
        private void LoadWaveOutDevices() { var devs = new List<WaveOutDeviceItem> { new WaveOutDeviceItem(-1, "(По умолчанию)") }; for (int i = 0; i < WaveOut.DeviceCount; i++) devs.Add(new WaveOutDeviceItem(i, WaveOut.GetCapabilities(i).ProductName)); if (s_cmbMonitor != null) { s_cmbMonitor.Items.Clear(); foreach (var d in devs) s_cmbMonitor.Items.Add(d); s_cmbMonitor.SelectedIndex = 0; } if (s_cmbMicOut != null) { s_cmbMicOut.Items.Clear(); foreach (var d in devs) s_cmbMicOut.Items.Add(d); s_cmbMicOut.SelectedIndex = 0; } }
        private void LoadWaveInDevices() { if (s_cmbMicIn == null) return; s_cmbMicIn.Items.Clear(); s_cmbMicIn.Items.Add(new WaveInDeviceItem(-1, "(Микрофон по умолчанию)")); for (int i = 0; i < WaveIn.DeviceCount; i++) s_cmbMicIn.Items.Add(new WaveInDeviceItem(i, WaveIn.GetCapabilities(i).ProductName)); s_cmbMicIn.SelectedIndex = 0; }
        private int GetSelectedDeviceNumber(ComboBox? cb) => (cb?.SelectedItem is WaveOutDeviceItem item) ? item.DeviceNumber : -1;
        private IWavePlayer CreatePlayerForSound(int id, IWaveProvider prov) { try { var wo = new WaveOutEvent { DeviceNumber = id, DesiredLatency = OUT_LATENCY_MS }; wo.Init(prov); return wo; } catch { var wa = new WasapiOut(FindRenderDeviceForWaveOutNumber(id), AudioClientShareMode.Shared, false, OUT_LATENCY_MS); wa.Init(prov); return wa; } }
        private IWavePlayer CreatePlayerForMicOut(int id, IWaveProvider prov) { try { var wo = new WaveOutEvent { DeviceNumber = id, DesiredLatency = MIC_OUT_LATENCY_MS }; wo.Init(prov); return wo; } catch { var wa = new WasapiOut(FindRenderDeviceForWaveOutNumber(id), AudioClientShareMode.Shared, false, MIC_OUT_LATENCY_MS); wa.Init(prov); return wa; } }
        private static ISampleProvider EnsureMono(ISampleProvider src) { if (src.WaveFormat.Channels == 1) return src; return new StereoToMonoSampleProvider(src) { LeftVolume = 0.5f, RightVolume = 0.5f }; }
        private static MMDevice? FindRenderDeviceForWaveOutNumber(int id) { var en = new MMDeviceEnumerator(); if (id < 0) return en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia); string name = ""; try { name = WaveOut.GetCapabilities(id).ProductName; } catch { } var devs = en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active); return devs.FirstOrDefault(d => d.FriendlyName.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0) ?? devs.FirstOrDefault(d => name.IndexOf(d.FriendlyName, StringComparison.OrdinalIgnoreCase) >= 0); }
        private void StartMicPassthrough() { StopMicPassthrough(); bool rc = s_cbPassthrough?.Checked ?? false; bool rm = s_cbMicListen?.Checked ?? false; if (!rc && !rm) return; int micId = s_cmbMicIn?.SelectedItem is WaveInDeviceItem item ? item.DeviceNumber : -1; void TryStart(int sr) { realMicIn = new WaveInEvent { DeviceNumber = micId, WaveFormat = new WaveFormat(sr, 16, 1), BufferMilliseconds = MIC_IN_BUF_MS, NumberOfBuffers = 3 }; if (rc) { micBufferCable = new BufferedWaveProvider(realMicIn.WaveFormat) { DiscardOnBufferOverflow = true, ReadFully = true, BufferLength = realMicIn.WaveFormat.AverageBytesPerSecond }; micDspCable = new CustomDspProvider(micBufferCable.ToSampleProvider()); } if (rm) { micBufferMonitor = new BufferedWaveProvider(realMicIn.WaveFormat) { DiscardOnBufferOverflow = true, ReadFully = true, BufferLength = realMicIn.WaveFormat.AverageBytesPerSecond }; micDspMonitor = new CustomDspProvider(micBufferMonitor.ToSampleProvider()); } UpdateDspSettings(); if (rc && micDspCable != null) { realMicOutCable = CreatePlayerForMicOut(GetSelectedDeviceNumber(s_cmbMicOut), micDspCable.ToWaveProvider16()); realMicOutCable.Play(); } if (rm && micDspMonitor != null) { realMicOutMonitor = CreatePlayerForMicOut(GetSelectedDeviceNumber(s_cmbMonitor), micDspMonitor.ToWaveProvider16()); realMicOutMonitor.Play(); } realMicIn.DataAvailable += (s, a) => { micBufferCable?.AddSamples(a.Buffer, 0, a.BytesRecorded); micBufferMonitor?.AddSamples(a.Buffer, 0, a.BytesRecorded); float max = 0; for (int i = 0; i < a.BytesRecorded; i += 2) { float val = Math.Abs(BitConverter.ToInt16(a.Buffer, i) / 32768f); if (val > max) max = val; } if (max > micPeakLevel) micPeakLevel = max; }; realMicIn.StartRecording(); } try { TryStart(48000); } catch { try { TryStart(44100); } catch { StopMicPassthrough(); } } }
        private void StopMicPassthrough() { try { realMicIn?.StopRecording(); realMicIn?.Dispose(); } catch { } realMicIn = null; try { realMicOutCable?.Stop(); realMicOutCable?.Dispose(); } catch { } realMicOutCable = null; try { realMicOutMonitor?.Stop(); realMicOutMonitor?.Dispose(); } catch { } realMicOutMonitor = null; micPeakLevel = 0; }
        private void DuckUiTimer_Tick(object? sender, EventArgs e) { if (isInitializing) return; bool duck = s_cbDucking?.Checked ?? false; float thresh = (float)Math.Pow(1.0f - ((s_tbSens?.Value ?? 50) / 100f), 3); if (!duck) { duckingMultiplier = 1.0f; duckingPeakHold = 0; } else if (micPeakLevel >= thresh) { duckingMultiplier = Math.Max(duckingMultiplier - 0.15f, (s_tbDuckVol?.Value ?? 20) / 100f); duckingPeakHold = 15; } else if (duckingPeakHold > 0) duckingPeakHold--; else duckingMultiplier = Math.Min(duckingMultiplier + 0.05f, 1.0f); micPeakLevel = 0; ApplyVolumeToActiveReaders(); AudioFileReader? r; lock (activeReaders) r = activeReaders.Keys.LastOrDefault(); if (r != null && r.TotalTime.TotalSeconds > 0) progressBar1.Value = Math.Max(0, Math.Min(100, (int)((r.CurrentTime.TotalSeconds / r.TotalTime.TotalSeconds) * 100))); else progressBar1.Value = 0; }
        private void trackBar1_Scroll(object? sender, EventArgs e) => ApplyVolumeToActiveReaders();
        private void ApplyVolumeToActiveReaders() { if (isInitializing) return; float duck = (s_cbDucking?.Checked ?? false) ? duckingMultiplier : 1.0f; lock (activeReaders) { foreach (var kvp in activeReaders.ToList()) { float baseVol = (kvp.Value ? trackBarMic.Value : trackBar1.Value) / 100f; try { kvp.Key.Volume = Math.Clamp(baseVol * duck, 0.0005f, 1.0f); } catch { } } } }
        private void progressBar1_MouseDown(object? sender, MouseEventArgs e) { AudioFileReader? r; lock (activeReaders) r = activeReaders.Keys.LastOrDefault(); if (r != null && r.Length > 0) r.Position = (long)(Math.Clamp((float)e.X / Math.Max(1, progressBar1.Width), 0, 1) * r.Length); }
        private void SavePlaylist(string path) { try { if (currentProfile.Banks.Count == 0) currentProfile.Banks.Add(new SoundBank { Name = "Основной банк" }); File.WriteAllText(path, JsonSerializer.Serialize(currentProfile, new JsonSerializerOptions { WriteIndented = true })); } catch { } }
        private void LoadPlaylist(string path) { if (!File.Exists(path)) { currentProfile = new AuralistixProfile(); currentProfile.Banks.Add(new SoundBank { Name = "Основной банк" }); RefreshTreeView(); RefreshListView(); return; } try { string c = File.ReadAllText(path); if (c.Contains("|") && !c.TrimStart().StartsWith("{")) MigrateOldPlaylist(c); else currentProfile = JsonSerializer.Deserialize<AuralistixProfile>(c) ?? currentProfile; RefreshTreeView(); RefreshListView(); } catch { } }
        private void MigrateOldPlaylist(string c) { currentProfile = new AuralistixProfile(); var b = new SoundBank { Name = "Импорт" }; foreach (var l in c.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)) { var p = l.Split('|'); if (p.Length >= 3) b.Sounds.Add(new SoundItem { Category = p[0], Name = p[1], Path = p[2], Duration = p.Length > 3 ? p[3] : "00:00", BindKey = p.Length > 4 ? p[4] : "" }); } currentProfile.Banks.Add(b); AutoSaveProfile(); }
        private void button4_Click(object? sender, EventArgs e) { SpeakText(textBox1.Text); }
        private void button5_MouseDown(object? sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) StartBeep(); }
        private void button5_MouseUp(object? sender, MouseEventArgs e) { StopBeep(); }
        protected override void OnFormClosing(FormClosingEventArgs e) { AutoSaveProfile(); StopBeep(); StopMicPassthrough(); base.OnFormClosing(e); }
        private void StartBeep() { StopBeep(); try { if (checkBox4.Checked) beepOutMonitor = CreateBeep(GetSelectedDeviceNumber(s_cmbMonitor), false); if (CanSendSoundToMic()) beepOutMic = CreateBeep(GetSelectedDeviceNumber(s_cmbMicOut), true); } catch { StopBeep(); } }
        private WaveOutEvent CreateBeep(int dev, bool isMic) { var wo = new WaveOutEvent { DeviceNumber = dev, DesiredLatency = OUT_LATENCY_MS }; var vol = new VolumeSampleProvider(new SignalGenerator(44100, 1) { Type = SignalGeneratorType.Sin, Frequency = 1000, Gain = 0.2 }) { Volume = 0.6f * ((isMic ? trackBarMic.Value : trackBar1.Value) / 100f) }; wo.Init(vol.ToWaveProvider16()); wo.Play(); return wo; }
        private void StopBeep() { try { beepOutMonitor?.Stop(); beepOutMonitor?.Dispose(); beepOutMonitor = null; beepOutMic?.Stop(); beepOutMic?.Dispose(); beepOutMic = null; } catch { } }
        private void InitTts() { try { tts = new SpeechSynthesizer(); if (s_cmbVoice != null) { s_cmbVoice.Items.Clear(); foreach (var v in tts.GetInstalledVoices()) s_cmbVoice.Items.Add(v.VoiceInfo.Name); if (s_cmbVoice.Items.Count > 0) s_cmbVoice.SelectedIndex = 0; } } catch { tts = null; } }
        private async void SpeakText(string text) { if (string.IsNullOrWhiteSpace(text)) return; string v = s_cmbVoice?.SelectedItem as string ?? ""; int vol = Math.Clamp(trackBar1.Value, 0, 100); try { string p = Path.Combine(Path.GetTempPath(), $"tts_{Guid.NewGuid():N}.wav"); tempTtsFiles.Add(p); await Task.Run(() => { using var s = new SpeechSynthesizer(); if (!string.IsNullOrEmpty(v)) s.SelectVoice(v); s.Volume = vol; s.SetOutputToWaveFile(p); s.Speak(text.Trim()); }); PlaySoundItem(new SoundItem { Path = p }); } catch { } }
        private void Timer8D_Tick(object? sender, EventArgs e) { if (!checkBox3.Checked) return; lock (activePanners) foreach (var p in activePanners.ToList()) { if (!panDirections.TryGetValue(p, out var d)) d = panStep; p.Pan += d; if (p.Pan >= 1f || p.Pan <= -1f) { p.Pan = Math.Clamp(p.Pan, -1f, 1f); panDirections[p] = -d; } else panDirections[p] = d; } }
        private void InitEqPresetsUI() { if (s_cmbEqPresets != null) { s_cmbEqPresets.DropDownStyle = ComboBoxStyle.DropDownList; s_cmbEqPresets.SelectedIndexChanged += (s, e) => { if (!suppressEqUi) ApplySelectedEqPreset(); }; } void Mark() { if (s_cmbEqPresets != null && s_cmbEqPresets.SelectedIndex != 0 && !suppressEqUi) { suppressEqUi = true; s_cmbEqPresets.SelectedIndex = 0; suppressEqUi = false; } } if (s_tbLow != null) s_tbLow.Scroll += (s, e) => Mark(); if (s_tbMid != null) s_tbMid.Scroll += (s, e) => Mark(); if (s_tbHigh != null) s_tbHigh.Scroll += (s, e) => Mark(); if (s_btnEqSave != null) s_btnEqSave.Click += (s, e) => SaveEqPreset(); if (s_btnEqRename != null) s_btnEqRename.Click += (s, e) => RenameEqPreset(); if (s_btnEqDelete != null) s_btnEqDelete.Click += (s, e) => DeleteEqPreset(); }
        private void LoadEqPresets() { eqPresets.Clear(); eqPresets.Add(new EqPreset { Name = "Пользовательский", Low = s_tbLow?.Value ?? 0, Mid = s_tbMid?.Value ?? 0, High = s_tbHigh?.Value ?? 0 }); try { if (File.Exists(eqPresetsFilePath)) foreach (var l in File.ReadAllLines(eqPresetsFilePath)) { var p = l.Split('\t'); if (p.Length >= 4) eqPresets.Add(new EqPreset { Name = p[0], Low = int.Parse(p[1]), Mid = int.Parse(p[2]), High = int.Parse(p[3]) }); } } catch { } RefreshEqPresetCombo(); }
        private void SaveEqPresetsToFile() { try { File.WriteAllLines(eqPresetsFilePath, eqPresets.Where(p => p.Name != "Пользовательский").Select(p => $"{p.Name}\t{p.Low}\t{p.Mid}\t{p.High}")); } catch { } }
        private void RefreshEqPresetCombo() { if (s_cmbEqPresets == null) return; suppressEqUi = true; s_cmbEqPresets.Items.Clear(); foreach (var p in eqPresets) s_cmbEqPresets.Items.Add(p.Name); s_cmbEqPresets.SelectedIndex = 0; suppressEqUi = false; }
        private void ApplySelectedEqPreset() { if (s_cmbEqPresets == null || s_cmbEqPresets.SelectedIndex < 0) return; var p = eqPresets[s_cmbEqPresets.SelectedIndex]; suppressEqUi = true; if (s_tbLow != null) s_tbLow.Value = p.Low; if (s_tbMid != null) s_tbMid.Value = p.Mid; if (s_tbHigh != null) s_tbHigh.Value = p.High; suppressEqUi = false; UpdateDspSettings(); }
        private void SaveEqPreset() { string n = Interaction.InputBox("Имя:", "EQ", "").Trim(); if (string.IsNullOrEmpty(n) || n == "Пользовательский") return; var ex = eqPresets.FirstOrDefault(p => p.Name == n); if (ex != null) { ex.Low = s_tbLow?.Value ?? 0; ex.Mid = s_tbMid?.Value ?? 0; ex.High = s_tbHigh?.Value ?? 0; } else eqPresets.Add(new EqPreset { Name = n, Low = s_tbLow?.Value ?? 0, Mid = s_tbMid?.Value ?? 0, High = s_tbHigh?.Value ?? 0 }); SaveEqPresetsToFile(); RefreshEqPresetCombo(); s_cmbEqPresets!.SelectedIndex = eqPresets.FindIndex(p => p.Name == n); }
        private void RenameEqPreset() { if (s_cmbEqPresets == null || s_cmbEqPresets.SelectedIndex <= 0) return; var cur = eqPresets[s_cmbEqPresets.SelectedIndex]; string n = Interaction.InputBox("Новое имя:", "EQ", cur.Name).Trim(); if (!string.IsNullOrEmpty(n) && n != "Пользовательский" && !eqPresets.Any(p => p.Name == n)) { cur.Name = n; SaveEqPresetsToFile(); RefreshEqPresetCombo(); s_cmbEqPresets.SelectedIndex = eqPresets.FindIndex(p => p.Name == n); } }
        private void DeleteEqPreset() { if (s_cmbEqPresets == null || s_cmbEqPresets.SelectedIndex <= 0) return; eqPresets.RemoveAt(s_cmbEqPresets.SelectedIndex); SaveEqPresetsToFile(); RefreshEqPresetCombo(); }
        private void AutoSaveProfile() => SavePlaylist(currentProfilePath);

        public class CustomDspProvider : ISampleProvider
        {
            private readonly ISampleProvider source;
            public WaveFormat WaveFormat => source.WaveFormat;
            public bool Bypass { get; set; } = false;
            public bool EnableNormalize { get; set; } = false;
            public float NormalizeGain { get; set; } = 2.5f;
            public string EffectPreset { get; set; } = "Нет";

            private BiQuadFilter hpFilter = null!, lpFilter = null!, eqLow = null!, eqMid = null!, eqHigh = null!;
            private float[] delayBuffer; private int delayPos;

            public CustomDspProvider(ISampleProvider src) { source = src; delayBuffer = new float[(int)(WaveFormat.SampleRate * 0.25)]; }
            public void UpdateEq(float l, float m, float h) { eqLow = BiQuadFilter.PeakingEQ(WaveFormat.SampleRate, 100, 1, l); eqMid = BiQuadFilter.PeakingEQ(WaveFormat.SampleRate, 1000, 1, m); eqHigh = BiQuadFilter.PeakingEQ(WaveFormat.SampleRate, 5000, 1, h); }
            public int Read(float[] buf, int off, int cnt)
            {
                int r = source.Read(buf, off, cnt); if (Bypass) return r;
                if (EffectPreset.Contains("Радио") && hpFilter == null) { hpFilter = BiQuadFilter.HighPassFilter(WaveFormat.SampleRate, 1000, 1.2f); lpFilter = BiQuadFilter.LowPassFilter(WaveFormat.SampleRate, 3000, 1.2f); }
                for (int i = 0; i < r; i++)
                {
                    float s = buf[off + i];
                    if (eqLow != null) s = eqLow.Transform(s); if (eqMid != null) s = eqMid.Transform(s); if (eqHigh != null) s = eqHigh.Transform(s);
                    if (EffectPreset.Contains("Радио") && hpFilter != null) s = lpFilter.Transform(hpFilter.Transform(s)) * 4;
                    else if (EffectPreset.Contains("Дисторшн")) s = (float)Math.Tanh(s * 10);
                    else if (EffectPreset.Contains("Эхо")) { float ds = delayBuffer[delayPos]; delayBuffer[delayPos] = s + ds * 0.5f; s = s * 0.7f + ds * 0.4f; delayPos = (delayPos + 1) % delayBuffer.Length; }
                    if (EnableNormalize) s = Math.Clamp(s * NormalizeGain, -0.95f, 0.95f);
                    buf[off + i] = s;
                }
                return r;
            }
        }

        private sealed class AnimatedHoverTip : Form
        {
            private readonly Label label; private readonly System.Windows.Forms.Timer animTimer; private double targetOpacity = 0.0; private bool isShown = false;
            public AnimatedHoverTip() { FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual; TopMost = true; Opacity = 0; BackColor = Color.FromArgb(30, 30, 30); label = new Label { AutoSize = true, ForeColor = Color.White, BackColor = Color.Transparent, MaximumSize = new Size(550, 0), Padding = new Padding(10), Font = new Font("Segoe UI", 9F) }; Controls.Add(label); animTimer = new System.Windows.Forms.Timer { Interval = 15 }; animTimer.Tick += (s, e) => { double cur = Opacity; if (Math.Abs(cur - targetOpacity) < 0.02) { Opacity = targetOpacity; animTimer.Stop(); if (Opacity <= 0) Hide(); return; } Opacity = cur < targetOpacity ? Math.Min(cur + 0.1, 1) : Math.Max(cur - 0.1, 0); }; }
            protected override bool ShowWithoutActivation => true;

            // ИСПРАВЛЕНИЕ: ДОБАВЛЕН ФЛАГ WS_EX_TRANSPARENT (0x00000020), чтобы мышь пролетала сквозь окно
            protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ExStyle |= 0x08000000; cp.ExStyle |= 0x00000080; cp.ExStyle |= 0x00000020; return cp; } }

            public void ShowTip(string t, Point p) { label.Text = t; using (var g = CreateGraphics()) { var sz = TextRenderer.MeasureText(g, t, label.Font, new Size(550, 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding); label.Size = new Size(sz.Width + label.Padding.Horizontal, sz.Height + label.Padding.Vertical); } ClientSize = label.Size; Location = p; isShown = true; targetOpacity = 1.0; if (!Visible) Show(); animTimer.Start(); }
            public void UpdatePosition(Point p) { if (isShown || Visible) Location = p; }
            public void HideTip() { if (!Visible && !isShown) return; isShown = false; targetOpacity = 0.0; animTimer.Start(); }
        }
    }

    // --- МОДЕЛИ ДАННЫХ ---
    public enum PlaybackMode { Normal, HoldToPlay, Toggle }

    public class SoundBank
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = "Основной банк";
        public List<SoundItem> Sounds { get; set; } = new List<SoundItem>();
        public List<SoundBank> SubBanks { get; set; } = new List<SoundBank>();
    }

    public class SoundItem
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public string Duration { get; set; } = "00:00";
        public string Category { get; set; } = "None";

        public List<string> Tags { get; set; } = new List<string>();
        public bool IsFavorite { get; set; } = false;
        public DateTime DateAdded { get; set; } = DateTime.Now;
        public DateTime LastPlayed { get; set; }
        public int PlayCount { get; set; } = 0;

        public string BindKey { get; set; } = string.Empty;
        public PlaybackMode PlayMode { get; set; } = PlaybackMode.Normal;

        public float VolumeMultiplier { get; set; } = 1.0f;
        public int FadeInMs { get; set; } = 0;
        public int FadeOutMs { get; set; } = 0;
        public double TrimStartSec { get; set; } = 0;
        public double TrimEndSec { get; set; } = 0;
        public bool CrossfadeLoop { get; set; } = false;
    }

    public class AuralistixProfile
    {
        public string ProfileName { get; set; } = "Default";
        public List<SoundBank> Banks { get; set; } = new List<SoundBank>();
    }
}
