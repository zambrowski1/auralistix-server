using System;
using System.Windows.Forms;

namespace Auralistix
{
    public partial class SettingsForm : Form
    {
        public SettingsForm()
        {
            InitializeComponent();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                this.Hide();
            }
            base.OnFormClosing(e);
        }

        // --- ВСТАВЬ ЭТИ ДВЕ СТРОКИ, ЧТОБЫ УБРАТЬ ОШИБКИ СО СКРИНШОТА ---
        public void label2_Click(object sender, EventArgs e) { }
        public void checkBox6_CheckedChanged(object sender, EventArgs e) { }
    }
}