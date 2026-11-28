using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace ClaudeCurtain
{
    /// <summary>
    /// The only way a PIN gets set: typed by a person into this dialog. The PIN is hashed and discarded;
    /// it is never passed on a command line, logged, or written anywhere in plain text.
    /// </summary>
    internal sealed class SetPinDialog : Form
    {
        private readonly string pinFile;
        private readonly TextBox pin1 = new TextBox();
        private readonly TextBox pin2 = new TextBox();
        private readonly Label status = new Label();
        private readonly Button save = new Button();
        private readonly System.Windows.Forms.Timer closeTimer = new System.Windows.Forms.Timer();
        private bool saved;
        private readonly float scale = 1f;

        private int Px(int v) { return (int)Math.Round(v * scale); }

        private void Place(Control c, int x, int y, int w, int h)
        {
            c.SetBounds(Px(x), Px(y), Px(w), Px(h));
        }

        public SetPinDialog(string pinFile)
        {
            this.pinFile = pinFile;

            SuspendLayout();
            // Scale the layout ourselves from the screen's DPI. WinForms' own AutoScaleMode.Dpi, in this
            // per-monitor-aware process, scaled the labels twice at 150% (2.25x) but the text boxes once,
            // so the intro label covered the New PIN box. The font is in points, so it scales on its own.
            AutoScaleMode = AutoScaleMode.None;
            // The screen's DPI, read without creating this form's handle (which would lose CenterScreen).
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) scale = g.DpiX / 96f;
            Text = "Claude curtain - set unlock PIN";
            Font = new Font("Segoe UI", 9.75f);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(Px(460), Px(286));
            TopMost = true;

            Label intro = new Label();
            Place(intro, 18, 14, 424, 66);
            intro.Text = "Choose the PIN that unlocks the privacy curtain from this keyboard.\r\n" +
                         PinStore.MinLength + " to " + PinStore.MaxLength + " digits. " +
                         "Do not reuse a PIN you use anywhere else.";

            Label label1 = new Label();
            Place(label1, 18, 92, 110, 24);
            label1.Text = "New PIN";
            pin1.Name = "pin1";
            Place(pin1, 134, 89, 190, 26);
            pin1.UseSystemPasswordChar = true;
            pin1.MaxLength = PinStore.MaxLength;
            pin1.KeyPress += DigitsOnly;

            Label label2 = new Label();
            Place(label2, 18, 128, 110, 24);
            label2.Text = "Repeat PIN";
            pin2.Name = "pin2";
            Place(pin2, 134, 125, 190, 26);
            pin2.UseSystemPasswordChar = true;
            pin2.MaxLength = PinStore.MaxLength;
            pin2.KeyPress += DigitsOnly;

            Place(status, 18, 162, 424, 40);
            status.ForeColor = Color.Firebrick;
            if (File.Exists(pinFile))
            {
                status.ForeColor = Color.DimGray;
                status.Text = "A PIN is already set. Saving replaces it.";
            }

            Label note = new Label();
            Place(note, 18, 206, 424, 22);
            note.ForeColor = Color.DimGray;
            note.Text = "Only a salted hash is stored, in the tool's local folder.";

            save.Name = "save";
            save.Text = "Save PIN";
            Place(save, 232, 238, 100, 32);
            save.Click += OnSave;

            Button cancel = new Button();
            cancel.Name = "cancel";
            cancel.Text = "Cancel";
            Place(cancel, 342, 238, 100, 32);
            cancel.Click += delegate { Close(); };

            AcceptButton = save;
            CancelButton = cancel;
            Controls.AddRange(new Control[] { intro, label1, pin1, label2, pin2, status, note, save, cancel });
            ResumeLayout(false);
            PerformLayout();

            closeTimer.Interval = 1100;
            closeTimer.Tick += delegate { closeTimer.Stop(); Close(); };
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Activate();
            pin1.Focus();
        }

        private static void DigitsOnly(object sender, KeyPressEventArgs e)
        {
            if (!char.IsControl(e.KeyChar) && (e.KeyChar < '0' || e.KeyChar > '9')) e.Handled = true;
        }

        private void OnSave(object sender, EventArgs e)
        {
            if (saved) return;
            status.ForeColor = Color.Firebrick;

            string why;
            if (!PinStore.IsValidPin(pin1.Text, out why))
            {
                status.Text = why;
                pin1.Focus();
                return;
            }
            if (pin1.Text != pin2.Text)
            {
                status.Text = "The two entries do not match.";
                pin2.Clear();
                pin2.Focus();
                return;
            }

            try
            {
                PinStore.Save(pinFile, pin1.Text);
            }
            catch (Exception ex)
            {
                status.Text = "Could not save: " + ex.Message;
                Log.Write("set-pin: save failed: " + ex.Message);
                return;
            }

            saved = true;
            pin1.Clear();
            pin2.Clear();
            pin1.Enabled = false;
            pin2.Enabled = false;
            save.Enabled = false;
            status.ForeColor = Color.SeaGreen;
            status.Text = "PIN saved.";
            Log.Write("set-pin: a new PIN hash was saved");
            closeTimer.Start();
        }

        public static int Run(Options o)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // Typing into this dialog is impossible while the curtain blocks the keyboard, and the running
            // curtain would keep using the old PIN anyway.
            bool createdNew;
            using (Mutex probe = new Mutex(false, CurtainApp.MutexName, out createdNew))
            {
            }
            if (!createdNew)
            {
                MessageBox.Show("The curtain is running. Turn it off first, then set the PIN.",
                                "Claude curtain", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return ExitCode.AlreadyRunning;
            }

            using (SetPinDialog dialog = new SetPinDialog(o.PinFile))
            {
                Application.Run(dialog);
                return dialog.saved ? ExitCode.Ok : ExitCode.Usage;
            }
        }
    }
}
