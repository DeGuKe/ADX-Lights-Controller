// SPDX-License-Identifier: MIT
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO.Ports;
using System.Globalization;
using System.Windows.Forms;

internal static class AdxLightsController
{
    private const string AppName = "ADX Lights Controller";
    private const string AppVersion = "1.0.0";
    private const string PortName = "COM21";
    private const int BaudRate = 115200;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--self-test")
        {
            byte[] frame = Frame(0x31, new byte[] { 0, 0, 0, 0 });
            string actual = BitConverter.ToString(frame);
            if (actual != "E0-11-01-05-31-00-00-00-00-48")
            {
                Console.Error.WriteLine("组帧测试失败: " + actual);
                return 1;
            }
            string escaped = BitConverter.ToString(Frame(0x31, new byte[] { 0, 0xE0, 0xD0, 0 }));
            if (escaped != "E0-11-01-05-31-00-D0-DF-D0-CF-00-F8")
            {
                Console.Error.WriteLine("转义测试失败: " + escaped);
                return 1;
            }
            using (RingSelector selector = new RingSelector())
            {
                if (selector.IndexAt(new Point(186, 45)) != 0 || selector.IndexAt(new Point(245, 104)) != 1 ||
                    selector.IndexAt(new Point(245, 186)) != 2 || selector.IndexAt(new Point(186, 245)) != 3 ||
                    selector.IndexAt(new Point(104, 245)) != 4 || selector.IndexAt(new Point(45, 186)) != 5 ||
                    selector.IndexAt(new Point(45, 104)) != 6 || selector.IndexAt(new Point(104, 45)) != 7)
                {
                    Console.Error.WriteLine("环形编号位置测试失败。");
                    return 1;
                }
                selector.ToggleAt(new Point(186, 45));
                selector.ToggleAt(new Point(245, 186));
                selector.ToggleAt(new Point(104, 245));
                if (selector.SelectedMask != 0x15)
                {
                    Console.Error.WriteLine("多选测试失败: " + selector.SelectedMask);
                    return 1;
                }
                selector.ToggleAt(new Point(245, 186));
                if (selector.SelectedMask != 0x11)
                {
                    Console.Error.WriteLine("取消选中测试失败: " + selector.SelectedMask);
                    return 1;
                }
            }
            using (LightForm form = new LightForm())
            {
                if (!form.SelfTestColorMode())
                {
                    Console.Error.WriteLine("颜色来源切换测试失败。");
                    return 1;
                }
            }
            using (AboutForm about = new AboutForm())
            {
                if (about.Text.IndexOf(AppName, StringComparison.Ordinal) < 0)
                {
                    Console.Error.WriteLine("关于窗口测试失败。");
                    return 1;
                }
            }
            Console.WriteLine("组帧测试通过: " + actual);
            return 0;
        }

        if (args.Length == 1 && args[0] == "--off")
        {
            try { SendColors(0xFF, Color.Black, true); Console.WriteLine("已向 COM21 发送熄灯命令。"); return 0; }
            catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
        }

        if (args.Length == 1 && args[0] == "--white")
        {
            try { SendColors(0xFF, Color.White, true); Console.WriteLine("已向 COM21 发送白灯命令。"); return 0; }
            catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
        }

        if (args.Length == 1 && args[0] == "--about")
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new AboutForm());
            return 0;
        }

        if (args.Length != 0)
        {
            Console.Error.WriteLine("用法: ADX-Lights-Controller.exe [--off | --white | --about | --self-test]");
            return 2;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new LightForm());
        return 0;
    }

    private static bool GameRunning()
    {
        foreach (string name in new string[] { "Sinmai", "amdaemon" })
        {
            Process[] processes = Process.GetProcessesByName(name);
            try { if (processes.Length > 0) return true; }
            finally { foreach (Process process in processes) process.Dispose(); }
        }
        return false;
    }

    private static byte[] Frame(byte command, byte[] payload)
    {
        // SEGA 837-15070: E0 | destination | source | length | command | payload | sum.
        // Except E0, E0 and D0 are escaped as D0 followed by (byte - 1).
        byte[] plain = new byte[6 + payload.Length];
        plain[0] = 0xE0;
        plain[1] = 0x11;
        plain[2] = 0x01;
        plain[3] = (byte)(payload.Length + 1);
        plain[4] = command;
        Array.Copy(payload, 0, plain, 5, payload.Length);
        int sum = 0;
        for (int i = 1; i < plain.Length - 1; i++) sum += plain[i];
        plain[plain.Length - 1] = (byte)sum;

        System.Collections.Generic.List<byte> encoded = new System.Collections.Generic.List<byte>();
        encoded.Add(0xE0);
        for (int i = 1; i < plain.Length; i++)
        {
            if (plain[i] == 0xE0 || plain[i] == 0xD0)
            {
                encoded.Add(0xD0);
                encoded.Add((byte)(plain[i] - 1));
            }
            else encoded.Add(plain[i]);
        }
        return encoded.ToArray();
    }

    private static void WriteFrame(SerialPort port, byte command, params byte[] payload)
    {
        if (GameRunning()) throw new InvalidOperationException("游戏或 AMDaemon 已启动；已停止发送并释放串口。游戏会自行控制灯光。");
        byte[] frame = Frame(command, payload);
        port.Write(frame, 0, frame.Length);
    }

    private static void SendColors(int selectedMask, Color color, bool includeCabinet)
    {
        if (GameRunning()) throw new InvalidOperationException("游戏或 AMDaemon 正在运行；现在由游戏控制灯光。");
        if ((selectedMask & ~0xFF) != 0 || (selectedMask == 0 && !includeCabinet))
            throw new ArgumentOutOfRangeException("selectedMask", "没有选中外键。");
        using (SerialPort port = new SerialPort(PortName, BaudRate, Parity.None, 8, StopBits.One))
        {
            port.Handshake = Handshake.None;
            port.DtrEnable = false;
            port.RtsEnable = false;
            port.ReadTimeout = 300;
            port.WriteTimeout = 500;
            port.Open();
            for (int i = 0; i < 8; i++)
                if ((selectedMask & (1 << i)) != 0)
                    WriteFrame(port, 0x31, (byte)i, color.R, color.G, color.B);
            if (includeCabinet)
            {
                // Some 15070 variants map cabinet LEDs to GS channels 8/9;
                // others use the three FET outputs. Cover both mappings.
                WriteFrame(port, 0x31, 8, color.R, color.G, color.B);
                WriteFrame(port, 0x31, 9, color.R, color.G, color.B);
                WriteFrame(port, 0x39, color.R, color.G, color.B);
            }
            WriteFrame(port, 0x3C); // GS update
        }
    }

    private sealed class RingSelector : Control
    {
        private int selectedMask;
        internal event EventHandler SelectionChanged;

        internal int SelectedMask
        {
            get { return selectedMask; }
            set
            {
                value &= 0xFF;
                if (selectedMask == value) return;
                selectedMask = value;
                Invalidate();
                if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty);
            }
        }

        internal RingSelector()
        {
            Size = new Size(290, 290);
            DoubleBuffered = true;
            Cursor = Cursors.Hand;
            BackColor = Color.FromArgb(248, 250, 252);
            TabStop = true;
        }

        private GraphicsPath Segment(float centerAngle)
        {
            float start = centerAngle - 10.65f;
            const float sweep = 21.3f;
            RectangleF outer = new RectangleF(8, 8, 274, 274);
            RectangleF inner = new RectangleF(54, 54, 182, 182);
            GraphicsPath path = new GraphicsPath();
            path.AddArc(outer, start, sweep);
            path.AddArc(inner, start + sweep, -sweep);
            path.CloseFigure();
            return path;
        }

        private GraphicsPath Sector(int index)
        {
            // The eight lit keycaps alternate with eight pale frame panels.
            // A1 is upper right; numbering proceeds clockwise to A8.
            return Segment(-67.5f + index * 45f);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button != MouseButtons.Left) return;
            ToggleAt(e.Location);
        }

        internal int IndexAt(Point location)
        {
            for (int i = 0; i < 8; i++)
            {
                using (GraphicsPath path = Sector(i))
                {
                    if (path.IsVisible(location)) return i;
                }
            }
            return -1;
        }

        internal void ToggleAt(Point location)
        {
            int index = IndexAt(location);
            if (index < 0) return;
            SelectedMask ^= 1 << index;
            Focus();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            using (SolidBrush shadow = new SolidBrush(Color.FromArgb(212, 228, 234)))
                g.FillEllipse(shadow, 9, 12, 274, 274);
            using (SolidBrush rim = new SolidBrush(Color.FromArgb(16, 40, 53)))
            using (Pen neon = new Pen(Color.FromArgb(21, 198, 216), 2.4f))
            {
                g.FillEllipse(rim, 1, 1, 288, 288);
                g.DrawEllipse(neon, 3, 3, 284, 284);
            }

            for (int i = 0; i < 8; i++)
            {
                using (GraphicsPath palePanel = Segment(-90f + i * 45f))
                using (SolidBrush paleFill = new SolidBrush(Color.FromArgb(242, 246, 248)))
                using (Pen paleEdge = new Pen(Color.FromArgb(182, 196, 205), 1.2f))
                {
                    g.FillPath(paleFill, palePanel);
                    g.DrawPath(paleEdge, palePanel);
                }

                bool selected = (selectedMask & (1 << i)) != 0;
                using (GraphicsPath path = Sector(i))
                using (SolidBrush fill = new SolidBrush(selected ? Color.FromArgb(48, 199, 242) : Color.FromArgb(207, 237, 252)))
                using (Pen border = new Pen(selected ? Color.FromArgb(12, 127, 181) : Color.FromArgb(105, 187, 225), 1.5f))
                {
                    g.FillPath(fill, path);
                    g.DrawPath(border, path);
                }

                double angle = (-67.5 + i * 45) * Math.PI / 180.0;
                float x = 145 + (float)(Math.Cos(angle) * 116);
                float y = 145 + (float)(Math.Sin(angle) * 116);
                using (Font numberFont = new Font("Microsoft YaHei UI", 11f, FontStyle.Bold))
                using (SolidBrush ink = new SolidBrush(selected ? Color.White : Color.FromArgb(40, 91, 117)))
                using (StringFormat centered = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString((i + 1).ToString(), numberFont, ink, new RectangleF(x - 17, y - 17, 34, 34), centered);

                double panelAngle = (-90 + i * 45) * Math.PI / 180.0;
                float dotX = 145 + (float)(Math.Cos(panelAngle) * 114);
                float dotY = 145 + (float)(Math.Sin(panelAngle) * 114);
                using (SolidBrush bolt = new SolidBrush(Color.FromArgb(184, 196, 201)))
                    g.FillEllipse(bolt, dotX - 2, dotY - 2, 4, 4);
            }

            using (SolidBrush center = new SolidBrush(Color.FromArgb(24, 53, 68)))
            using (Pen edge = new Pen(Color.FromArgb(85, 167, 192), 2f))
            {
                g.FillEllipse(center, 55, 55, 180, 180);
                g.DrawEllipse(edge, 55, 55, 180, 180);
            }
        }
    }

    private sealed class AboutForm : Form
    {
        internal AboutForm()
        {
            Text = "关于 - " + AppName;
            ClientSize = new Size(438, 260);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Color.FromArgb(248, 250, 252);
            Font = new Font("Microsoft YaHei UI", 9F);

            Controls.Add(new Label { Left = 20, Top = 18, Width = 398, Height = 34,
                Font = new Font("Microsoft YaHei UI", 15F, FontStyle.Bold), Text = AppName });
            Controls.Add(new Label { Left = 20, Top = 57, Width = 398, Height = 24,
                Text = "版本 v" + AppVersion });
            Controls.Add(new Label { Left = 20, Top = 91, Width = 398, Height = 29,
                Text = "一款用于ADX闲置灯光控制与测试的开源软件。" });
            Controls.Add(new Label { Left = 20, Top = 130, Width = 398, Height = 24,
                Text = "作者：DeGuKe" });

            Controls.Add(new Label { Left = 20, Top = 160, Width = 80, Height = 24, Text = "哔哩哔哩：" });
            AddLink("space.bilibili.com/15706430", "https://space.bilibili.com/15706430", 100, 160, 280);
            Controls.Add(new Label { Left = 20, Top = 189, Width = 80, Height = 24, Text = "GitHub：" });
            AddLink("github.com/DeGuKe", "https://github.com/DeGuKe", 100, 189, 280);

            Controls.Add(new Label { Left = 20, Top = 227, Width = 230, Height = 24,
                Text = "© 2026 DeGuKe · MIT License" });
            Button close = new Button { Left = 335, Top = 220, Width = 83, Height = 30,
                Text = "关闭", DialogResult = DialogResult.OK };
            Controls.Add(close);
            AcceptButton = close;
        }

        private void AddLink(string label, string url, int x, int y, int width)
        {
            LinkLabel link = new LinkLabel { Left = x, Top = y, Width = width, Height = 24, Text = label };
            link.Links.Add(0, label.Length, url);
            link.LinkClicked += delegate(object sender, LinkLabelLinkClickedEventArgs e)
            {
                try
                {
                    Process.Start(new ProcessStartInfo { FileName = (string)e.Link.LinkData,
                        UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "无法打开链接：" + ex.Message, AppName,
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };
            Controls.Add(link);
        }
    }

    private sealed class LightForm : Form
    {
        private sealed class Preset
        {
            internal readonly string Name;
            internal readonly Color Color;
            internal Preset(string name, int rgb)
            {
                Name = name;
                Color = Color.FromArgb((rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255);
            }
            public override string ToString()
            {
                return Name + "  #" + Color.R.ToString("X2") + Color.G.ToString("X2") + Color.B.ToString("X2");
            }
        }

        private readonly Label state;
        private readonly Label result;
        private readonly Label selectionStatus;
        private readonly Label currentColorName;
        private readonly RingSelector ring;
        private readonly CheckBox cabinet;
        private readonly RadioButton presetMode;
        private readonly RadioButton customMode;
        private readonly ComboBox preset;
        private readonly TextBox customHex;
        private readonly Panel preview;
        private readonly Button offAll;
        private readonly Button whiteAll;
        private readonly Button onSelected;
        private readonly Button offSelected;
        private readonly Button pickColor;
        private readonly Button aboutButton;
        private readonly Timer timer;
        private Color currentColor = Color.FromArgb(255, 226, 0);
        private bool currentColorValid = true;

        internal LightForm()
        {
            Text = AppName;
            ClientSize = new Size(680, 470);
            MinimumSize = new Size(696, 509);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            BackColor = Color.FromArgb(248, 250, 252);
            Font = new Font("Microsoft YaHei UI", 9F);

            state = new Label { Left = 16, Top = 15, Width = 648, Height = 25 };
            Controls.Add(state);
            Controls.Add(new Label { Left = 16, Top = 43, Width = 648, Height = 39,
                Text = "点击圆环上的外键，可同时选中多个；选择颜色来源后点击“点亮选中”。\r\n每次操作后立即释放 COM21，游戏启动时自行接管灯光。" });

            ring = new RingSelector { Left = 18, Top = 98 };
            ring.SelectionChanged += delegate { RefreshState(); };
            Controls.Add(ring);
            Controls.Add(new Label { Left = 41, Top = 402, Width = 260, Height = 25,
                Text = "1 号在右上角，按顺时针排列到 8 号" });

            whiteAll = MakeButton("全部白灯", 326, 98, 159, 38);
            whiteAll.Click += delegate { Apply(0xFF, Color.White, true); };
            offAll = MakeButton("关闭全部灯光", 499, 98, 165, 38);
            offAll.Click += delegate { Apply(0xFF, Color.Black, true); };

            selectionStatus = new Label { Left = 326, Top = 148, Width = 338, Height = 25 };
            Controls.Add(selectionStatus);
            Button selectAll = MakeButton("全选八键", 326, 175, 159, 32);
            selectAll.Click += delegate { ring.SelectedMask = 0xFF; };
            Button clear = MakeButton("清空选择", 499, 175, 165, 32);
            clear.Click += delegate { ring.SelectedMask = 0; };

            presetMode = new RadioButton { Left = 326, Top = 218, Width = 160, Height = 24,
                Text = "预设颜色", Checked = true };
            presetMode.CheckedChanged += delegate { if (presetMode.Checked) UpdateColorSelection(); };
            Controls.Add(presetMode);
            preset = new ComboBox { Left = 326, Top = 243, Width = 338, DropDownStyle = ComboBoxStyle.DropDownList };
            preset.Items.AddRange(new object[] {
                new Preset("游戏 CRITICAL PERFECT（黄）", 0xFFE200),
                new Preset("游戏 PERFECT（橙黄）", 0xFF8A00),
                new Preset("游戏 GREAT（粉红）", 0xFF0078),
                new Preset("游戏 GOOD（绿）", 0x00E400),
                new Preset("游戏 MASTER（紫）", 0xCE00FF),
                new Preset("游戏 Re:MASTER（粉紫）", 0xFF40FF),
                new Preset("游戏按键反应（白）", 0xFFFFFF),
                new Preset("游戏菜单主色（蓝）", 0x0028FF),
                new Preset("游戏菜单次色（绿）", 0x28FF00),
                new Preset("游戏设置（紫）", 0xBF22EC),
                new Preset("游戏 FESTA（金）", 0xFABE50),
                new Preset("标准红", 0xFF0000),
                new Preset("标准绿", 0x00FF00),
                new Preset("标准蓝", 0x0000FF),
                new Preset("标准黄", 0xFFFF00),
                new Preset("标准青", 0x00FFFF),
                new Preset("标准紫", 0x8000FF)
            });
            preset.SelectedIndexChanged += delegate { presetMode.Checked = true; UpdateColorSelection(); };
            Controls.Add(preset);

            customMode = new RadioButton { Left = 326, Top = 280, Width = 160, Height = 24, Text = "自选 RGB" };
            customMode.CheckedChanged += delegate { if (customMode.Checked) UpdateColorSelection(); };
            Controls.Add(customMode);
            customHex = new TextBox { Left = 326, Top = 309, Width = 124, MaxLength = 7, Text = "#00FFFF" };
            customHex.TextChanged += delegate { customMode.Checked = true; UpdateColorSelection(); };
            customHex.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter) { UpdateColorSelection(); e.SuppressKeyPress = true; }
            };
            Controls.Add(customHex);
            pickColor = MakeButton("从调色板选择…", 464, 305, 200, 32);
            pickColor.Click += delegate
            {
                using (ColorDialog dialog = new ColorDialog { FullOpen = true, Color = currentColor })
                {
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;
                    customHex.Text = "#" + dialog.Color.R.ToString("X2") + dialog.Color.G.ToString("X2") + dialog.Color.B.ToString("X2");
                    customMode.Checked = true;
                    UpdateColorSelection();
                }
            };

            preview = new Panel { Left = 326, Top = 351, Width = 32, Height = 27,
                BorderStyle = BorderStyle.FixedSingle, BackColor = currentColor };
            Controls.Add(preview);
            currentColorName = new Label { Left = 367, Top = 353, Width = 141, Height = 24 };
            Controls.Add(currentColorName);
            cabinet = new CheckBox { Left = 516, Top = 352, Width = 148, Height = 25, Text = "框体灯跟随" };
            Controls.Add(cabinet);

            onSelected = MakeButton("点亮选中", 326, 389, 159, 38);
            onSelected.Click += delegate { Apply(ring.SelectedMask, currentColor, cabinet.Checked); };
            offSelected = MakeButton("熄灭选中", 499, 389, 165, 38);
            offSelected.Click += delegate { Apply(ring.SelectedMask, Color.Black, cabinet.Checked); };

            result = new Label { Left = 16, Top = 436, Width = 555, Height = 30,
                Text = "亮蓝色代表选中；灯板不提供可靠的当前颜色读取，请观察实际灯光。" };
            Controls.Add(result);
            aboutButton = MakeButton("关于", 586, 434, 78, 30);
            aboutButton.Click += delegate
            {
                using (AboutForm about = new AboutForm()) about.ShowDialog(this);
            };

            preset.SelectedIndex = 0;
            timer = new Timer { Interval = 1000 };
            timer.Tick += delegate { RefreshState(); };
            timer.Start();
            RefreshState();
        }

        private Button MakeButton(string title, int x, int y, int width, int height)
        {
            Button button = new Button { Text = title, Left = x, Top = y, Width = width, Height = height,
                BackColor = Color.White, FlatStyle = FlatStyle.Flat };
            button.FlatAppearance.BorderColor = Color.FromArgb(185, 198, 208);
            Controls.Add(button);
            return button;
        }

        private bool TryReadCustomColor(out Color color)
        {
            string hex = customHex.Text.Trim().TrimStart('#');
            int value;
            if (hex.Length != 6 || !int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value))
            {
                color = Color.Empty;
                return false;
            }
            color = Color.FromArgb((value >> 16) & 255, (value >> 8) & 255, value & 255);
            return true;
        }

        private void UpdateColorSelection()
        {
            if (presetMode.Checked)
            {
                if (preset.SelectedItem == null) return;
                currentColor = ((Preset)preset.SelectedItem).Color;
                currentColorValid = true;
                currentColorName.Text = "预设 #" + currentColor.R.ToString("X2") + currentColor.G.ToString("X2") + currentColor.B.ToString("X2");
            }
            else
            {
                Color parsed;
                currentColorValid = TryReadCustomColor(out parsed);
                if (currentColorValid)
                {
                    currentColor = parsed;
                    currentColorName.Text = "自选 #" + parsed.R.ToString("X2") + parsed.G.ToString("X2") + parsed.B.ToString("X2");
                }
                else currentColorName.Text = "RGB 格式有误";
            }
            preview.BackColor = currentColorValid ? currentColor : Color.LightGray;
            RefreshState();
        }

        internal bool SelfTestColorMode()
        {
            if (!presetMode.Checked || !currentColorValid || currentColor.ToArgb() != Color.FromArgb(255, 226, 0).ToArgb()) return false;
            customHex.Text = "#112233";
            if (!customMode.Checked || !currentColorValid || currentColor.ToArgb() != Color.FromArgb(0x11, 0x22, 0x33).ToArgb()) return false;
            customHex.Text = "#12";
            if (!customMode.Checked || currentColorValid) return false;
            preset.SelectedIndex = 1;
            return presetMode.Checked && currentColorValid && currentColor.ToArgb() == Color.FromArgb(255, 138, 0).ToArgb();
        }

        private void Apply(int selectedMask, Color color, bool includeCabinet)
        {
            if (selectedMask == 0)
            {
                result.ForeColor = Color.DarkRed;
                result.Text = "请先在圆环中选择至少一个外键。";
                return;
            }
            try
            {
                SendColors(selectedMask, color, includeCabinet);
                result.ForeColor = Color.DarkGreen;
                result.Text = "命令已发送；关闭窗口不会继续占用 COM21。";
            }
            catch (Exception ex)
            {
                result.ForeColor = Color.DarkRed;
                result.Text = ex.Message;
            }
            RefreshState();
        }

        private void RefreshState()
        {
            bool playing = GameRunning();
            int count = 0;
            for (int i = 0; i < 8; i++) if ((ring.SelectedMask & (1 << i)) != 0) count++;
            selectionStatus.Text = "测试对象：已选 " + count + " 键（点击外键切换）";
            state.Text = playing ? "状态：游戏或 AMDaemon 运行中，灯光控制已暂停" : "状态：游戏未运行，可通过 COM21 控制灯光";
            state.ForeColor = playing ? Color.DarkOrange : Color.DarkGreen;
            offAll.Enabled = whiteAll.Enabled = !playing;
            onSelected.Enabled = !playing && ring.SelectedMask != 0 && currentColorValid;
            offSelected.Enabled = !playing && ring.SelectedMask != 0;
            pickColor.Enabled = !playing;
        }
    }

}
