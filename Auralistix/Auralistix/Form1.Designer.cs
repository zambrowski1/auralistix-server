using System;
using System.Drawing;
using System.Windows.Forms;

namespace Auralistix
{
    partial class Form1
    {
        private System.ComponentModel.IContainer components = null;

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
                components.Dispose();
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        private void InitializeComponent()
        {
            components = new System.ComponentModel.Container();
            button1 = new Button();
            contextMenuStrip1 = new ContextMenuStrip(components);
            воспроизвестиToolStripMenuItem = new ToolStripMenuItem();
            переименоватьToolStripMenuItem = new ToolStripMenuItem();
            удалитьToolStripMenuItem = new ToolStripMenuItem();
            покраситьВКатегориюToolStripMenuItem = new ToolStripMenuItem();
            красныйToolStripMenuItem = new ToolStripMenuItem();
            жёлтыйToolStripMenuItem = new ToolStripMenuItem();
            синийToolStripMenuItem = new ToolStripMenuItem();
            button2 = new Button();
            button3 = new Button();
            checkBox1 = new CheckBox();
            checkBox2 = new CheckBox();
            trackBar1 = new TrackBar();
            checkBox3 = new CheckBox();
            textBox1 = new TextBox();
            button4 = new Button();
            button5 = new Button();
            menuStrip1 = new MenuStrip();
            профильToolStripMenuItem = new ToolStripMenuItem();
            загрузитьПрофильToolStripMenuItem = new ToolStripMenuItem();
            сохранитьПрофильToolStripMenuItem = new ToolStripMenuItem();
            очиститьПлейлистToolStripMenuItem = new ToolStripMenuItem();
            menuSettingsBtn = new ToolStripMenuItem();
            progressBar1 = new ProgressBar();
            uiTimer = new System.Windows.Forms.Timer(components);
            checkBox4 = new CheckBox();
            listView1 = new ListView();
            trackBarMic = new TrackBar();
            treeViewBanks = new TreeView();
            btnAddBank = new Button();
            btnDelBank = new Button();
            txtSearch = new TextBox();
            contextMenuStrip1.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)trackBar1).BeginInit();
            menuStrip1.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)trackBarMic).BeginInit();
            SuspendLayout();
            // 
            // button1
            // 
            button1.Location = new Point(559, 117);
            button1.Name = "button1";
            button1.Size = new Size(94, 29);
            button1.TabIndex = 0;
            button1.Text = "Тест";
            button1.UseVisualStyleBackColor = true;
            // 
            // contextMenuStrip1
            // 
            contextMenuStrip1.ImageScalingSize = new Size(20, 20);
            contextMenuStrip1.Items.AddRange(new ToolStripItem[] { воспроизвестиToolStripMenuItem, переименоватьToolStripMenuItem, удалитьToolStripMenuItem, покраситьВКатегориюToolStripMenuItem });
            contextMenuStrip1.Name = "contextMenuStrip1";
            contextMenuStrip1.Size = new Size(243, 100);
            // 
            // воспроизвестиToolStripMenuItem
            // 
            воспроизвестиToolStripMenuItem.Name = "воспроизвестиToolStripMenuItem";
            воспроизвестиToolStripMenuItem.Size = new Size(242, 24);
            воспроизвестиToolStripMenuItem.Text = "Воспроизвести";
            // 
            // переименоватьToolStripMenuItem
            // 
            переименоватьToolStripMenuItem.Name = "переименоватьToolStripMenuItem";
            переименоватьToolStripMenuItem.Size = new Size(242, 24);
            переименоватьToolStripMenuItem.Text = "Переименовать";
            // 
            // удалитьToolStripMenuItem
            // 
            удалитьToolStripMenuItem.Name = "удалитьToolStripMenuItem";
            удалитьToolStripMenuItem.Size = new Size(242, 24);
            удалитьToolStripMenuItem.Text = "Удалить";
            // 
            // покраситьВКатегориюToolStripMenuItem
            // 
            покраситьВКатегориюToolStripMenuItem.DropDownItems.AddRange(new ToolStripItem[] { красныйToolStripMenuItem, жёлтыйToolStripMenuItem, синийToolStripMenuItem });
            покраситьВКатегориюToolStripMenuItem.Name = "покраситьВКатегориюToolStripMenuItem";
            покраситьВКатегориюToolStripMenuItem.Size = new Size(242, 24);
            покраситьВКатегориюToolStripMenuItem.Text = "Покрасить в категорию";
            // 
            // красныйToolStripMenuItem
            // 
            красныйToolStripMenuItem.Name = "красныйToolStripMenuItem";
            красныйToolStripMenuItem.Size = new Size(154, 26);
            красныйToolStripMenuItem.Text = "Красный";
            // 
            // жёлтыйToolStripMenuItem
            // 
            жёлтыйToolStripMenuItem.Name = "жёлтыйToolStripMenuItem";
            жёлтыйToolStripMenuItem.Size = new Size(154, 26);
            жёлтыйToolStripMenuItem.Text = "Жёлтый";
            // 
            // синийToolStripMenuItem
            // 
            синийToolStripMenuItem.Name = "синийToolStripMenuItem";
            синийToolStripMenuItem.Size = new Size(154, 26);
            синийToolStripMenuItem.Text = "Синий";
            // 
            // button2
            // 
            button2.Location = new Point(559, 47);
            button2.Name = "button2";
            button2.Size = new Size(94, 29);
            button2.TabIndex = 2;
            button2.Text = "Добавить";
            button2.UseVisualStyleBackColor = true;
            // 
            // button3
            // 
            button3.Location = new Point(559, 82);
            button3.Name = "button3";
            button3.Size = new Size(94, 29);
            button3.TabIndex = 3;
            button3.Text = "Стоп";
            button3.UseVisualStyleBackColor = true;
            // 
            // checkBox1
            // 
            checkBox1.AutoSize = true;
            checkBox1.Location = new Point(560, 152);
            checkBox1.Name = "checkBox1";
            checkBox1.Size = new Size(103, 24);
            checkBox1.TabIndex = 4;
            checkBox1.Text = "Зациклить";
            checkBox1.UseVisualStyleBackColor = true;
            // 
            // checkBox2
            // 
            checkBox2.AutoSize = true;
            checkBox2.Location = new Point(560, 182);
            checkBox2.Name = "checkBox2";
            checkBox2.Size = new Size(112, 24);
            checkBox2.TabIndex = 5;
            checkBox2.Text = "Наложение";
            checkBox2.UseVisualStyleBackColor = true;
            // 
            // trackBar1
            // 
            trackBar1.Location = new Point(27, 285);
            trackBar1.Maximum = 100;
            trackBar1.Name = "trackBar1";
            trackBar1.Size = new Size(255, 56);
            trackBar1.TabIndex = 8;
            trackBar1.Value = 100;
            // 
            // checkBox3
            // 
            checkBox3.AutoSize = true;
            checkBox3.Location = new Point(748, 47);
            checkBox3.Name = "checkBox3";
            checkBox3.Size = new Size(50, 24);
            checkBox3.TabIndex = 9;
            checkBox3.Text = "8D";
            checkBox3.UseVisualStyleBackColor = true;
            // 
            // textBox1
            // 
            textBox1.Location = new Point(560, 266);
            textBox1.Name = "textBox1";
            textBox1.Size = new Size(164, 27);
            textBox1.TabIndex = 10;
            // 
            // button4
            // 
            button4.Location = new Point(559, 234);
            button4.Name = "button4";
            button4.Size = new Size(94, 29);
            button4.TabIndex = 11;
            button4.Text = "Озвучить";
            button4.UseVisualStyleBackColor = true;
            // 
            // button5
            // 
            button5.Location = new Point(27, 377);
            button5.Name = "button5";
            button5.Size = new Size(94, 29);
            button5.TabIndex = 12;
            button5.Text = "ПИК";
            button5.UseVisualStyleBackColor = true;
            // 
            // menuStrip1
            // 
            menuStrip1.ImageScalingSize = new Size(20, 20);
            menuStrip1.Items.AddRange(new ToolStripItem[] { профильToolStripMenuItem, menuSettingsBtn });
            menuStrip1.Location = new Point(0, 0);
            menuStrip1.Name = "menuStrip1";
            menuStrip1.Size = new Size(835, 28);
            menuStrip1.TabIndex = 16;
            menuStrip1.Text = "menuStrip1";
            // 
            // профильToolStripMenuItem
            // 
            профильToolStripMenuItem.DropDownItems.AddRange(new ToolStripItem[] { загрузитьПрофильToolStripMenuItem, сохранитьПрофильToolStripMenuItem, очиститьПлейлистToolStripMenuItem });
            профильToolStripMenuItem.Name = "профильToolStripMenuItem";
            профильToolStripMenuItem.Size = new Size(87, 24);
            профильToolStripMenuItem.Text = "Профиль";
            // 
            // загрузитьПрофильToolStripMenuItem
            // 
            загрузитьПрофильToolStripMenuItem.Name = "загрузитьПрофильToolStripMenuItem";
            загрузитьПрофильToolStripMenuItem.Size = new Size(232, 26);
            загрузитьПрофильToolStripMenuItem.Text = "Загрузить профиль";
            // 
            // сохранитьПрофильToolStripMenuItem
            // 
            сохранитьПрофильToolStripMenuItem.Name = "сохранитьПрофильToolStripMenuItem";
            сохранитьПрофильToolStripMenuItem.Size = new Size(232, 26);
            сохранитьПрофильToolStripMenuItem.Text = "Сохранить профиль";
            // 
            // очиститьПлейлистToolStripMenuItem
            // 
            очиститьПлейлистToolStripMenuItem.Name = "очиститьПлейлистToolStripMenuItem";
            очиститьПлейлистToolStripMenuItem.Size = new Size(232, 26);
            очиститьПлейлистToolStripMenuItem.Text = "Очистить плейлист";
            // 
            // menuSettingsBtn
            // 
            menuSettingsBtn.Name = "menuSettingsBtn";
            menuSettingsBtn.Size = new Size(98, 24);
            menuSettingsBtn.Text = "Настройки";
            // 
            // progressBar1
            // 
            progressBar1.Location = new Point(27, 338);
            progressBar1.Name = "progressBar1";
            progressBar1.Size = new Size(245, 23);
            progressBar1.TabIndex = 17;
            // 
            // checkBox4
            // 
            checkBox4.AutoSize = true;
            checkBox4.Checked = true;
            checkBox4.CheckState = CheckState.Checked;
            checkBox4.Location = new Point(559, 314);
            checkBox4.Name = "checkBox4";
            checkBox4.Size = new Size(72, 24);
            checkBox4.TabIndex = 20;
            checkBox4.Text = "В уши";
            checkBox4.UseVisualStyleBackColor = true;
            // 
            // listView1
            // 
            listView1.Location = new Point(93, 82);
            listView1.Name = "listView1";
            listView1.Size = new Size(440, 197);
            listView1.TabIndex = 21;
            listView1.UseCompatibleStateImageBehavior = false;
            // 
            // trackBarMic
            // 
            trackBarMic.Location = new Point(278, 282);
            trackBarMic.Maximum = 100;
            trackBarMic.Name = "trackBarMic";
            trackBarMic.Size = new Size(255, 56);
            trackBarMic.TabIndex = 22;
            trackBarMic.Value = 100;
            // 
            // treeViewBanks
            // 
            treeViewBanks.Location = new Point(12, 82);
            treeViewBanks.Name = "treeViewBanks";
            treeViewBanks.Size = new Size(75, 197);
            treeViewBanks.TabIndex = 23;
            // 
            // btnAddBank
            // 
            btnAddBank.Location = new Point(12, 50);
            btnAddBank.Name = "btnAddBank";
            btnAddBank.Size = new Size(37, 30);
            btnAddBank.TabIndex = 24;
            btnAddBank.Text = "+";
            btnAddBank.UseVisualStyleBackColor = true;
            // 
            // btnDelBank
            // 
            btnDelBank.Location = new Point(50, 50);
            btnDelBank.Name = "btnDelBank";
            btnDelBank.Size = new Size(37, 30);
            btnDelBank.TabIndex = 25;
            btnDelBank.Text = "-";
            btnDelBank.UseVisualStyleBackColor = true;
            // 
            // txtSearch
            // 
            txtSearch.Location = new Point(93, 52);
            txtSearch.Name = "txtSearch";
            txtSearch.PlaceholderText = "Поиск по названию или бинду...";
            txtSearch.Size = new Size(440, 27);
            txtSearch.TabIndex = 26;
            // 
            // Form1
            // 
            AutoScaleDimensions = new SizeF(8F, 20F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(835, 458);
            Controls.Add(txtSearch);
            Controls.Add(btnDelBank);
            Controls.Add(btnAddBank);
            Controls.Add(treeViewBanks);
            Controls.Add(trackBarMic);
            Controls.Add(listView1);
            Controls.Add(checkBox4);
            Controls.Add(progressBar1);
            Controls.Add(button5);
            Controls.Add(button4);
            Controls.Add(textBox1);
            Controls.Add(checkBox3);
            Controls.Add(trackBar1);
            Controls.Add(checkBox2);
            Controls.Add(checkBox1);
            Controls.Add(button3);
            Controls.Add(button2);
            Controls.Add(button1);
            Controls.Add(menuStrip1);
            MainMenuStrip = menuStrip1;
            Name = "Form1";
            Text = "Form1";
            contextMenuStrip1.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)trackBar1).EndInit();
            menuStrip1.ResumeLayout(false);
            menuStrip1.PerformLayout();
            ((System.ComponentModel.ISupportInitialize)trackBarMic).EndInit();
            ResumeLayout(false);
            PerformLayout();
        }

        #endregion

        private Button button1;
        private Button button2;
        private Button button3;
        private CheckBox checkBox1;
        private CheckBox checkBox2;
        private TrackBar trackBar1;
        private CheckBox checkBox3;
        private TextBox textBox1;
        private Button button4;
        private Button button5;

        private MenuStrip menuStrip1;
        private ToolStripMenuItem профильToolStripMenuItem;
        private ToolStripMenuItem загрузитьПрофильToolStripMenuItem;
        private ToolStripMenuItem сохранитьПрофильToolStripMenuItem;
        private ToolStripMenuItem очиститьПлейлистToolStripMenuItem;

        private ProgressBar progressBar1;

        private ContextMenuStrip contextMenuStrip1;
        private ToolStripMenuItem воспроизвестиToolStripMenuItem;
        private ToolStripMenuItem переименоватьToolStripMenuItem;
        private ToolStripMenuItem удалитьToolStripMenuItem;
        private ToolStripMenuItem покраситьВКатегориюToolStripMenuItem;
        private ToolStripMenuItem красныйToolStripMenuItem;
        private ToolStripMenuItem жёлтыйToolStripMenuItem;
        private ToolStripMenuItem синийToolStripMenuItem;

        private System.Windows.Forms.Timer uiTimer;
        private CheckBox checkBox4;
        private ToolStripMenuItem menuSettingsBtn;
        private ListView listView1;
        private TrackBar trackBarMic;
        private TreeView treeViewBanks;
        private Button btnAddBank;
        private Button btnDelBank;
        private TextBox txtSearch;
    }
}