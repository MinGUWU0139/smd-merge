using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;
using System.Drawing;

// SMD 动画拼接工具 v2.0（三文件版：主骨骼 + 机件动画 + 身体动画）
//
// 用法：主骨骼（提供骨骼层级与静止姿势）+ 机件动画（slide/clip/hammer/silencer/trigger）
//       + 身体动画（root_motion/wpn/手臂/手指），按骨骼名合成一份完整动画。
//   - 输出使用「主骨骼」的节点表；
//   - 身体动画里存在的骨骼 → 逐帧取身体动画的值；
//   - 机件骨骼（slide/clip/hammer/silencer/trigger）→ 逐帧取机件动画的值；
//   - 其余骨骼 → 取主骨骼同帧（没有该帧则取第 0 帧）；
//   - 帧数 = 两个动画文件帧数的较小值；可选位置放大倍数。
//
// 编译（系统自带 .NET Framework csc 即可）：
//   %WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:winexe /optimize+ /out:smd_merge.exe ^
//     /r:"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\System.Windows.Forms.dll" ^
//     /r:"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\System.Drawing.dll" smd_merge_gui.cs
//
// 命令行模式：
//   smd_merge.exe --cli <主骨骼.smd> <机件动画.smd> <身体动画.smd> [输出.smd] [--scale <倍数>]

class SmdMergeApp
{
    // ---------------- 数据模型 ----------------
    class Node { public int Index; public string Name; public int Parent; }
    class Bone { public int Index; public double Px, Py, Pz, Rx, Ry, Rz; public string[] Tok; }
    class SmdDoc
    {
        public List<Node> Nodes = new List<Node>();
        public List<Bone[]> Frames = new List<Bone[]>();
        public Dictionary<string, int> NameToIndex = new Dictionary<string, int>();
        public string[] IndexToName;
        public int BoneCount;
    }

    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static readonly string[] MechBones = new string[] { "slide", "clip", "hammer", "silencer", "trigger" };

    [STAThread]
    static int Main(string[] args)
    {
        if (Array.IndexOf(args, "--retarget") >= 0)
            return RunRetargetCli(args);
        if (Array.IndexOf(args, "--cli") >= 0)
            return RunCli(args);

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm());
        return 0;
    }

    // ---------------- 命令行模式 ----------------
    static int RunCli(string[] args)
    {
        List<string> files = new List<string>();
        double scale = 1.0;
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i].Trim();
            if (a.Length == 0 || a == "--cli") continue;
            if (a == "--scale" && i + 1 < args.Length)
            {
                double v;
                if (double.TryParse(args[++i], NumberStyles.Float, Inv, out v) && v > 0) scale = v;
                continue;
            }
            files.Add(a);
        }

        StringBuilder sb = new StringBuilder();
        int code = MergeFiles(files, scale, delegate(string s) { sb.Append(s).Append('\n'); });
        string outp = null;
        if (files.Count >= 4) outp = EnsureSmdExt(files[3]);
        else if (files.Count >= 1) outp = EnsureSmdExt(DefaultOutput(files[0]));
        if (outp != null) { try { File.WriteAllText(outp + ".log", sb.ToString(), new UTF8Encoding(false)); } catch { } }
        return code;
    }

    // 手臂重定向命令行：--retarget <动作源> <目标骨架> <输出>
    static int RunRetargetCli(string[] args)
    {
        List<string> files = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i].Trim();
            if (a.Length == 0 || a == "--retarget") continue;
            files.Add(a);
        }
        StringBuilder sb = new StringBuilder();
        int code = 1;
        try
        {
            if (files.Count < 2)
            {
                sb.Append("错误: 需要至少两个文件: 动作源 目标骨架 [输出]\n");
            }
            else
            {
                string outp = EnsureSmdExt(files.Count > 2 ? files[2] : DefaultRetargetOutput(files[0]));
                SleeveRetarget.Run(files[0], files[1], outp, delegate(string s) { sb.Append(s).Append('\n'); });
                code = 0;
                try { File.WriteAllText(outp + ".log", sb.ToString(), new UTF8Encoding(false)); } catch { }
            }
        }
        catch (Exception ex) { sb.Append("错误: ").Append(ex.Message).Append('\n'); }
        return code;
    }

    // 三文件拼接：主骨骼 + 机件动画 + 身体动画
    static int MergeFiles(List<string> files, double scale, Action<string> logSink)
    {
        if (files.Count < 3)
        {
            logSink("错误: 需要三个文件: 主骨骼 机件动画 身体动画 [输出]");
            return 1;
        }
        string basePath = files[0], mechPath = files[1], bodyPath = files[2];
        string outp = EnsureSmdExt(files.Count > 3 ? files[3] : DefaultOutput(basePath));
        try
        {
            foreach (string p in new string[] { basePath, mechPath, bodyPath })
            {
                if (string.Equals(Path.GetFullPath(outp), Path.GetFullPath(p), StringComparison.OrdinalIgnoreCase))
                {
                    logSink("错误: 输出文件不能与输入文件相同");
                    return 1;
                }
            }

            logSink("读取主骨骼: " + basePath);
            SmdDoc b = Parse(basePath);
            logSink("  主骨骼: " + b.Nodes.Count + " 骨骼, " + b.Frames.Count + " 帧");
            logSink("读取机件动画: " + mechPath);
            SmdDoc m = Parse(mechPath);
            logSink("  机件: " + m.Nodes.Count + " 骨骼, " + m.Frames.Count + " 帧");
            logSink("读取身体动画: " + bodyPath);
            SmdDoc d = Parse(bodyPath);
            logSink("  身体: " + d.Nodes.Count + " 骨骼, " + d.Frames.Count + " 帧");

            if (m.Frames.Count == 0 || d.Frames.Count == 0)
                logSink("警告: 动画文件没有帧数据，输出将只保留主骨骼姿势");

            List<Bone[]> frames = MergeThree(b, m, d, scale);
            string dir = Path.GetDirectoryName(outp);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            Write(b, frames, outp);

            logSink("拼接完成 → " + outp);
            logSink("  输出帧数: " + frames.Count);
            logSink("  输出骨骼: " + b.Nodes.Count);
            logSink("  身体骨骼: 取自 " + Path.GetFileName(bodyPath));
            logSink("  机件骨骼: 取自 " + Path.GetFileName(mechPath) + " (slide/clip/hammer/silencer/trigger)");
            logSink("  位置放大: " + scale + " 倍");
            return 0;
        }
        catch (Exception ex)
        {
            logSink("错误: " + ex.Message);
            return 1;
        }
    }

    // ---------------- 合并引擎 ----------------

    static List<Bone[]> MergeThree(SmdDoc baseDoc, SmdDoc mech, SmdDoc body, double scale)
    {
        int frames;
        if (mech.Frames.Count > 0 && body.Frames.Count > 0) frames = Math.Min(mech.Frames.Count, body.Frames.Count);
        else if (mech.Frames.Count > 0) frames = mech.Frames.Count;
        else if (body.Frames.Count > 0) frames = body.Frames.Count;
        else frames = Math.Max(1, baseDoc.Frames.Count);

        HashSet<string> bodyNames = new HashSet<string>(body.NameToIndex.Keys);
        bodyNames.Remove("blender_implicit"); // 根虚拟骨骼保留主骨骼的值（身体文件里常写成整数 0）
        HashSet<string> mechNames = new HashSet<string>(mech.NameToIndex.Keys);
        HashSet<string> mechSet = new HashSet<string>(MechBones);

        List<Bone[]> outFrames = new List<Bone[]>();
        for (int f = 0; f < frames; f++)
        {
            Bone[] rest = null;
            if (baseDoc.Frames.Count > 0) rest = (f < baseDoc.Frames.Count) ? baseDoc.Frames[f] : baseDoc.Frames[0];

            Bone[] of = new Bone[baseDoc.BoneCount];
            for (int k = 0; k < baseDoc.BoneCount; k++)
            {
                Bone tb = (rest != null && k < rest.Length) ? rest[k] : null;
                if (tb == null) continue;
                Bone nb = new Bone();
                nb.Index = tb.Index;
                nb.Px = tb.Px; nb.Py = tb.Py; nb.Pz = tb.Pz;
                nb.Rx = tb.Rx; nb.Ry = tb.Ry; nb.Rz = tb.Rz;
                nb.Tok = tb.Tok;
                of[k] = nb;
            }

            for (int k = 0; k < baseDoc.BoneCount; k++)
            {
                if (of[k] == null) continue;
                string nm = (baseDoc.IndexToName != null && k < baseDoc.IndexToName.Length) ? baseDoc.IndexToName[k] : null;
                if (nm == null) continue;
                if (bodyNames.Contains(nm) && f < body.Frames.Count)
                {
                    Bone[] bf = body.Frames[f];
                    int bi = body.NameToIndex[nm];
                    if (bi >= 0 && bi < bf.Length && bf[bi] != null)
                    {
                        Bone sb = bf[bi];
                        of[k].Px = sb.Px; of[k].Py = sb.Py; of[k].Pz = sb.Pz;
                        of[k].Rx = sb.Rx; of[k].Ry = sb.Ry; of[k].Rz = sb.Rz;
                        of[k].Tok = sb.Tok;
                    }
                }
                else if (mechSet.Contains(nm) && mechNames.Contains(nm) && f < mech.Frames.Count)
                {
                    Bone[] mf = mech.Frames[f];
                    int mi = mech.NameToIndex[nm];
                    if (mi >= 0 && mi < mf.Length && mf[mi] != null)
                    {
                        Bone sb = mf[mi];
                        of[k].Px = sb.Px; of[k].Py = sb.Py; of[k].Pz = sb.Pz;
                        of[k].Rx = sb.Rx; of[k].Ry = sb.Ry; of[k].Rz = sb.Rz;
                        of[k].Tok = sb.Tok;
                    }
                }
            }

            if (scale != 1.0)
            {
                for (int k = 0; k < of.Length; k++)
                {
                    if (of[k] == null) continue;
                    of[k].Px *= scale;
                    of[k].Py *= scale;
                    of[k].Pz *= scale;
                    of[k].Tok = null;
                }
            }
            outFrames.Add(of);
        }
        return outFrames;
    }

    static string DefaultOutput(string basePath)
    {
        string dir = Path.GetDirectoryName(basePath);
        string name = Path.GetFileNameWithoutExtension(basePath) + "_reload.smd";
        return string.IsNullOrEmpty(dir) ? name : Path.Combine(dir, name);
    }

    static string DefaultRetargetOutput(string srcPath)
    {
        string dir = Path.GetDirectoryName(srcPath);
        string name = Path.GetFileNameWithoutExtension(srcPath) + "_retarget.smd";
        return string.IsNullOrEmpty(dir) ? name : Path.Combine(dir, name);
    }

    static string EnsureSmdExt(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        if (path.EndsWith(".smd", StringComparison.OrdinalIgnoreCase)) return path;
        return path + ".smd";
    }

    // ---------------- 解析 ----------------

    static SmdDoc Parse(string path)
    {
        string[] lines = File.ReadAllLines(path);
        SmdDoc doc = new SmdDoc();

        int i = 0;
        while (i < lines.Length && !lines[i].Trim().Equals("nodes", StringComparison.OrdinalIgnoreCase)) i++;
        i++;
        while (i < lines.Length && !lines[i].Trim().Equals("end", StringComparison.OrdinalIgnoreCase))
        {
            string line = lines[i].Trim();
            if (line.Length > 0) doc.Nodes.Add(ParseNode(line));
            i++;
        }
        i++;
        doc.BoneCount = doc.Nodes.Count;
        doc.IndexToName = new string[doc.BoneCount];
        for (int k = 0; k < doc.Nodes.Count; k++)
        {
            Node n = doc.Nodes[k];
            if (!doc.NameToIndex.ContainsKey(n.Name)) doc.NameToIndex[n.Name] = n.Index;
            if (n.Index >= 0 && n.Index < doc.BoneCount) doc.IndexToName[n.Index] = n.Name;
        }

        while (i < lines.Length && !lines[i].Trim().Equals("skeleton", StringComparison.OrdinalIgnoreCase)) i++;
        i++;
        Bone[] cur = null;
        while (i < lines.Length)
        {
            string line = lines[i].Trim();
            i++;
            if (line.Length == 0) continue;
            if (line.Equals("end", StringComparison.OrdinalIgnoreCase)) break;
            if (line.StartsWith("time", StringComparison.OrdinalIgnoreCase))
            {
                if (cur != null) doc.Frames.Add(cur);
                cur = new Bone[doc.BoneCount];
                continue;
            }
            if (cur == null) continue;
            Bone b = ParseBone(line);
            if (b.Index >= 0 && b.Index < doc.BoneCount) cur[b.Index] = b;
        }
        if (cur != null) doc.Frames.Add(cur);
        return doc;
    }

    static Node ParseNode(string line)
    {
        Node n = new Node();
        int sp = line.IndexOf(' ');
        n.Index = int.Parse(line.Substring(0, sp).Trim(), Inv);
        int q1 = line.IndexOf('"');
        int q2 = line.LastIndexOf('"');
        n.Name = line.Substring(q1 + 1, q2 - q1 - 1);
        n.Parent = int.Parse(line.Substring(q2 + 1).Trim(), Inv);
        return n;
    }

    static Bone ParseBone(string line)
    {
        string[] p = line.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        Bone b = new Bone();
        b.Index = int.Parse(p[0], Inv);
        b.Px = double.Parse(p[1], Inv);
        b.Py = double.Parse(p[2], Inv);
        b.Pz = double.Parse(p[3], Inv);
        b.Rx = double.Parse(p[4], Inv);
        b.Ry = double.Parse(p[5], Inv);
        b.Rz = double.Parse(p[6], Inv);
        b.Tok = new string[] { p[1], p[2], p[3], p[4], p[5], p[6] };
        return b;
    }

    // ---------------- 写入 ----------------

    static string F(double v)
    {
        return v.ToString("0.000000", Inv);
    }

    static void Write(SmdDoc tgt, List<Bone[]> frames, string outPath)
    {
        List<string> outLines = new List<string>();
        outLines.Add("version 1");
        outLines.Add("nodes");
        for (int k = 0; k < tgt.Nodes.Count; k++)
        {
            Node n = tgt.Nodes[k];
            outLines.Add(n.Index + " \"" + n.Name + "\" " + n.Parent);
        }
        outLines.Add("end");
        outLines.Add("skeleton");
        for (int f = 0; f < frames.Count; f++)
        {
            outLines.Add("time " + f);
            Bone[] frame = frames[f];
            for (int k = 0; k < frame.Length; k++)
            {
                Bone b = frame[k];
                if (b == null) continue;
                string vals;
                if (b.Tok != null && b.Tok.Length == 6)
                    vals = b.Tok[0] + " " + b.Tok[1] + " " + b.Tok[2] + "  " + b.Tok[3] + " " + b.Tok[4] + " " + b.Tok[5];
                else
                    vals = F(b.Px) + " " + F(b.Py) + " " + F(b.Pz) + "  " + F(b.Rx) + " " + F(b.Ry) + " " + F(b.Rz);
                outLines.Add(b.Index + "  " + vals);
            }
        }
        outLines.Add("end");
        File.WriteAllLines(outPath, outLines.ToArray(), new UTF8Encoding(false));
    }

    // ---------------- 图形界面 ----------------

    class MainForm : Form
    {
        TextBox baseBox = new TextBox();
        TextBox mechBox = new TextBox();
        TextBox bodyBox = new TextBox();
        TextBox outBox = new TextBox();
        TextBox scaleBox = new TextBox();
        Button goBtn = new Button();
        TextBox logBox = new TextBox();

        // 页 2：手臂重定向
        TextBox srcBox = new TextBox();
        TextBox tgtBox = new TextBox();
        TextBox out2Box = new TextBox();
        Button retargetBtn = new Button();

        TabControl tabs;
        Control host;

        public MainForm()
        {
            Text = "SMD 动画拼接工具 v2.1（拼接 + 手臂重定向）";
            ClientSize = new Size(760, 664);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei", 9f);
            AllowDrop = true;
            KeyPreview = true;

            tabs = new TabControl();
            tabs.Location = new Point(8, 8);
            tabs.Size = new Size(744, 404);
            Controls.Add(tabs);

            TabPage tab1 = new TabPage("动画拼接（三文件）");
            tabs.TabPages.Add(tab1);
            TabPage tab2 = new TabPage("手臂重定向");
            tabs.TabPages.Add(tab2);

            // ---------------- 页 1：三文件拼接 ----------------
            host = tab1;
            AddLabel("① 主骨骼 / 基底文件（提供骨骼层级和静止姿势，如 usp_s.smd）", 8, 14);
            SetupTextBox(baseBox, 8, 38, 500);
            AddButton("浏览...", 516, 38, 90, OnBrowseBase);
            AddButton("自动配对", 612, 38, 116, OnAutoPair);

            AddLabel("② 机件动画（提供 slide / clip / hammer / silencer / trigger）", 8, 78);
            SetupTextBox(mechBox, 8, 102, 500);
            AddButton("浏览...", 516, 102, 90, OnBrowseMech);

            AddLabel("③ 身体动画（提供 root_motion / wpn / 手臂 / 手指）", 8, 142);
            SetupTextBox(bodyBox, 8, 166, 500);
            AddButton("浏览...", 516, 166, 90, OnBrowseBody);

            AddLabel("④ 输出文件（留空则自动生成到主骨骼旁，文件名 _reload.smd）", 8, 206);
            SetupTextBox(outBox, 8, 230, 500);
            AddButton("另存为...", 516, 230, 90, OnBrowseOut);

            AddLabel("⑤ 位置放大倍数（骨骼位置坐标乘以该值，1 = 不放大）", 8, 270);
            scaleBox.Location = new Point(380, 266);
            scaleBox.Size = new Size(80, 24);
            scaleBox.Text = "1";
            tab1.Controls.Add(scaleBox);

            goBtn.Text = "开始拼接  (回车)";
            goBtn.Location = new Point(8, 306);
            goBtn.Size = new Size(720, 46);
            goBtn.Font = new Font("Microsoft YaHei", 11f, FontStyle.Bold);
            goBtn.Click += new EventHandler(OnGo);
            tab1.Controls.Add(goBtn);

            // ---------------- 页 2：手臂重定向 ----------------
            host = tab2;
            AddLabel("① 动作源（含手臂动画的 SMD，如 1.smd）", 8, 14);
            SetupTextBox(srcBox, 8, 38, 500);
            AddButton("浏览...", 516, 38, 90, OnBrowseSrc2);

            AddLabel("② 目标骨架（目标模型静止姿势，如 idle.smd / v_sleeve_ct.smd）", 8, 78);
            SetupTextBox(tgtBox, 8, 102, 500);
            AddButton("浏览...", 516, 102, 90, OnBrowseTgt2);

            AddLabel("③ 输出文件（留空则自动生成到动作源旁，文件名 _retarget.smd）", 8, 142);
            SetupTextBox(out2Box, 8, 166, 500);
            AddButton("另存为...", 516, 166, 90, OnBrowseOut2);

            retargetBtn.Text = "开始重定向  (回车)";
            retargetBtn.Location = new Point(8, 210);
            retargetBtn.Size = new Size(720, 46);
            retargetBtn.Font = new Font("Microsoft YaHei", 11f, FontStyle.Bold);
            retargetBtn.Click += new EventHandler(OnRetargetGo);
            tab2.Controls.Add(retargetBtn);

            AddLabel("说明：只输出动画（nodes + skeleton），位置沿用目标模型静止值，", 8, 268);
            AddLabel("旋转按静止姿势坐标系里的世界空间增量重定向。", 8, 290);

            // ---------------- 日志（两页共用） ----------------
            host = this;
            logBox.Multiline = true;
            logBox.ReadOnly = true;
            logBox.ScrollBars = ScrollBars.Vertical;
            logBox.Location = new Point(8, 420);
            logBox.Size = new Size(744, 232);
            logBox.BackColor = Color.White;
            Controls.Add(logBox);

            DragEnter += new DragEventHandler(OnFormDragEnter);
            DragDrop += new DragEventHandler(OnFormDragDrop);
            KeyDown += new KeyEventHandler(OnFormKeyDown);
            FormClosing += new FormClosingEventHandler(OnFormClosing);

            LoadSettings();
        }

        void AddLabel(string text, int x, int y)
        {
            Label l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.Location = new Point(x, y);
            host.Controls.Add(l);
        }

        void SetupTextBox(TextBox tb, int x, int y, int w)
        {
            tb.Location = new Point(x, y);
            tb.Size = new Size(w, 24);
            tb.AllowDrop = true;
            tb.DragEnter += new DragEventHandler(delegate(object s, DragEventArgs e)
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy;
            });
            tb.DragDrop += new DragEventHandler(delegate(object s, DragEventArgs e)
            {
                string[] fs = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (fs != null && fs.Length > 0) tb.Text = fs[0];
            });
            host.Controls.Add(tb);
        }

        void AddButton(string text, int x, int y, int w, EventHandler h)
        {
            Button b = new Button();
            b.Text = text;
            b.Location = new Point(x, y);
            b.Size = new Size(w, 26);
            b.Click += h;
            host.Controls.Add(b);
        }

        void OnBrowseBase(object s, EventArgs e) { BrowseInto(baseBox, "选择主骨骼 / 基底文件"); AutoFillOutput(); }
        void OnBrowseMech(object s, EventArgs e) { BrowseInto(mechBox, "选择机件动画"); }
        void OnBrowseBody(object s, EventArgs e) { BrowseInto(bodyBox, "选择身体动画"); }

        void BrowseInto(TextBox tb, string title)
        {
            OpenFileDialog d = new OpenFileDialog();
            d.Filter = "SMD 文件 (*.smd)|*.smd|所有文件 (*.*)|*.*";
            d.Title = title;
            string cur = tb.Text.Trim();
            if (cur.Length > 0) { try { d.InitialDirectory = Path.GetDirectoryName(cur); } catch { } }
            if (d.ShowDialog(this) == DialogResult.OK) tb.Text = d.FileName;
        }

        void OnBrowseOut(object s, EventArgs e)
        {
            SaveFileDialog d = new SaveFileDialog();
            d.Filter = "SMD 文件 (*.smd)|*.smd";
            d.DefaultExt = "smd";
            d.AddExtension = true;
            d.Title = "选择输出文件";
            if (outBox.Text.Trim().Length > 0) d.FileName = outBox.Text.Trim();
            else if (baseBox.Text.Trim().Length > 0) d.FileName = DefaultOutput(baseBox.Text.Trim());
            if (d.ShowDialog(this) == DialogResult.OK) outBox.Text = d.FileName;
        }

        // ---------------- 页 2：手臂重定向 ----------------
        void OnBrowseSrc2(object s, EventArgs e) { BrowseInto(srcBox, "选择动作源（含手臂动画的 SMD）"); AutoFillRetargetOutput(); }
        void OnBrowseTgt2(object s, EventArgs e) { BrowseInto(tgtBox, "选择目标骨架（静止姿势 SMD）"); AutoFillRetargetOutput(); }

        void OnBrowseOut2(object s, EventArgs e)
        {
            SaveFileDialog d = new SaveFileDialog();
            d.Filter = "SMD 文件 (*.smd)|*.smd";
            d.DefaultExt = "smd";
            d.AddExtension = true;
            d.Title = "选择输出文件";
            if (out2Box.Text.Trim().Length > 0) d.FileName = out2Box.Text.Trim();
            else if (srcBox.Text.Trim().Length > 0) d.FileName = DefaultRetargetOutput(srcBox.Text.Trim());
            if (d.ShowDialog(this) == DialogResult.OK) out2Box.Text = d.FileName;
        }

        void AutoFillRetargetOutput()
        {
            string sp = srcBox.Text.Trim();
            if (sp.Length > 0 && out2Box.Text.Trim().Length == 0) out2Box.Text = DefaultRetargetOutput(sp);
        }

        void OnRetargetGo(object s, EventArgs e)
        {
            string src = srcBox.Text.Trim();
            string tgt = tgtBox.Text.Trim();
            string outp = out2Box.Text.Trim();

            if (src.Length == 0) { Log("错误: 请先选择动作源"); return; }
            if (tgt.Length == 0) { Log("错误: 请先选择目标骨架"); return; }
            if (!File.Exists(src)) { Log("错误: 动作源不存在: " + src); return; }
            if (!File.Exists(tgt)) { Log("错误: 目标骨架不存在: " + tgt); return; }
            if (outp.Length == 0) { outp = DefaultRetargetOutput(src); out2Box.Text = outp; }

            try
            {
                if (string.Equals(Path.GetFullPath(outp), Path.GetFullPath(src), StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(Path.GetFullPath(outp), Path.GetFullPath(tgt), StringComparison.OrdinalIgnoreCase))
                {
                    Log("错误: 输出文件不能与输入文件相同");
                    return;
                }
            }
            catch { }

            retargetBtn.Enabled = false;
            Cursor = Cursors.WaitCursor;
            try
            {
                SleeveRetarget.Run(src, tgt, outp, Log);
                SaveSettings();
                MessageBox.Show(this, "重定向完成！\n\n输出文件:\n" + outp, "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                Log("错误: " + ex.Message);
                MessageBox.Show(this, "重定向失败，详情见下方日志。", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                Cursor = Cursors.Default;
                retargetBtn.Enabled = true;
            }
        }

        void OnAutoPair(object s, EventArgs e)
        {
            string basePath = baseBox.Text.Trim();
            if (basePath.Length == 0 || !File.Exists(basePath)) { Log("错误: 请先选择主骨骼文件"); return; }
            string dir = Path.GetDirectoryName(basePath);
            List<string> cands = new List<string>();
            try { cands.AddRange(Directory.GetFiles(dir, "*.smd")); } catch { }
            try
            {
                string sub = Path.Combine(dir, "anims");
                if (Directory.Exists(sub)) cands.AddRange(Directory.GetFiles(sub, "*.smd"));
            }
            catch { }

            string bestMech = null, bestBody = null;
            int bestMechBones = -1, bestBodyBones = -1;
            foreach (string f in cands)
            {
                if (string.Equals(Path.GetFullPath(f), Path.GetFullPath(basePath), StringComparison.OrdinalIgnoreCase)) continue;
                SmdDoc d = TryParse(f);
                if (d == null || d.Frames.Count < 2) continue;
                bool hasMech = d.NameToIndex.ContainsKey("slide") && d.NameToIndex.ContainsKey("hammer");
                bool hasBody = d.NameToIndex.ContainsKey("arm_lower_L") || d.NameToIndex.ContainsKey("hand_L");
                if (hasMech && hasBody) continue;
                if (hasMech)
                {
                    if (bestMech == null || d.Nodes.Count < bestMechBones) { bestMech = f; bestMechBones = d.Nodes.Count; }
                }
                else if (hasBody)
                {
                    if (bestBody == null || d.Nodes.Count > bestBodyBones) { bestBody = f; bestBodyBones = d.Nodes.Count; }
                }
            }
            if (bestMech != null) { mechBox.Text = bestMech; Log("机件动画: " + bestMech); }
            if (bestBody != null) { bodyBox.Text = bestBody; Log("身体动画: " + bestBody); }
            if (bestMech == null && bestBody == null) Log("没找到可配对的机件/身体动画（需要同目录或 anims 子目录下带动画的 .smd）");
            AutoFillOutput();
        }

        void OnGo(object s, EventArgs e)
        {
            string basePath = baseBox.Text.Trim();
            string mech = mechBox.Text.Trim();
            string body = bodyBox.Text.Trim();
            string outp = outBox.Text.Trim();

            if (basePath.Length == 0) { Log("错误: 请先选择主骨骼文件"); return; }
            if (mech.Length == 0) { Log("错误: 请先选择机件动画"); return; }
            if (body.Length == 0) { Log("错误: 请先选择身体动画"); return; }
            if (!File.Exists(basePath)) { Log("错误: 主骨骼不存在: " + basePath); return; }
            if (!File.Exists(mech)) { Log("错误: 机件动画不存在: " + mech); return; }
            if (!File.Exists(body)) { Log("错误: 身体动画不存在: " + body); return; }
            if (outp.Length == 0) { outp = DefaultOutput(basePath); outBox.Text = outp; }

            double scale = 1.0;
            string st = scaleBox.Text.Trim();
            if (st.Length > 0)
            {
                if (!double.TryParse(st, NumberStyles.Float, Inv, out scale) || scale <= 0)
                {
                    Log("错误: 放大倍数必须是大于 0 的数字");
                    return;
                }
            }

            goBtn.Enabled = false;
            Cursor = Cursors.WaitCursor;
            try
            {
                List<string> files = new List<string>();
                files.Add(basePath); files.Add(mech); files.Add(body); files.Add(outp);
                int code = MergeFiles(files, scale, delegate(string m) { Log(m); });
                if (code == 0)
                {
                    SaveSettings();
                    MessageBox.Show(this, "拼接完成！\n\n输出文件:\n" + outp, "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else
                {
                    MessageBox.Show(this, "拼接失败，详情见下方日志。", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            finally
            {
                Cursor = Cursors.Default;
                goBtn.Enabled = true;
            }
        }

        void Log(string m)
        {
            logBox.AppendText(m + "\r\n");
            logBox.SelectionStart = logBox.TextLength;
            logBox.ScrollToCaret();
        }

        void OnFormDragEnter(object s, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy;
            else e.Effect = DragDropEffects.None;
        }

        void OnFormDragDrop(object s, DragEventArgs e)
        {
            string[] fs = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (fs != null && fs.Length > 0) AssignFiles(fs);
        }

        void OnFormKeyDown(object s, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter && !e.Shift && !e.Control && !e.Alt)
            {
                e.SuppressKeyPress = true;
                if (tabs != null && tabs.SelectedIndex == 1) OnRetargetGo(null, EventArgs.Empty);
                else OnGo(null, EventArgs.Empty);
            }
        }

        void OnFormClosing(object s, FormClosingEventArgs e)
        {
            SaveSettings();
        }

        // 拖入文件：按骨骼数排序，最大 = 主骨骼，次大 = 身体，最小 = 机件
        void AssignFiles(string[] files)
        {
            List<string> list = new List<string>();
            foreach (string f in files) if (File.Exists(f)) list.Add(f);
            if (list.Count == 0) return;

            if (tabs != null && tabs.SelectedIndex == 1)
            {
                srcBox.Text = list[0];
                if (list.Count > 1) tgtBox.Text = list[1];
                AutoFillRetargetOutput();
                Log("拖入 " + list.Count + " 个文件 → 动作源: " + Path.GetFileName(srcBox.Text.Trim()) +
                    " / 目标: " + Path.GetFileName(tgtBox.Text.Trim()));
                return;
            }

            List<string> sorted = new List<string>(list);
            sorted.Sort(delegate(string a, string b)
            {
                int ca = NodeCount(a), cb = NodeCount(b);
                return cb.CompareTo(ca);
            });

            if (sorted.Count == 1)
            {
                if (baseBox.Text.Trim().Length == 0) baseBox.Text = sorted[0];
                else if (mechBox.Text.Trim().Length == 0) mechBox.Text = sorted[0];
                else bodyBox.Text = sorted[0];
            }
            else if (sorted.Count == 2)
            {
                baseBox.Text = sorted[0];
                if (LooksMech(sorted[1])) mechBox.Text = sorted[1];
                else bodyBox.Text = sorted[1];
            }
            else
            {
                baseBox.Text = sorted[0];
                bodyBox.Text = sorted[1];
                mechBox.Text = sorted[2];
            }
            AutoFillOutput();
            Log("拖入 " + sorted.Count + " 个文件 → 主骨骼: " + Path.GetFileName(baseBox.Text.Trim()) +
                " / 机件: " + Path.GetFileName(mechBox.Text.Trim()) +
                " / 身体: " + Path.GetFileName(bodyBox.Text.Trim()));
        }

        int NodeCount(string p)
        {
            SmdDoc d = TryParse(p);
            return d == null ? 0 : d.Nodes.Count;
        }

        bool LooksMech(string p)
        {
            SmdDoc d = TryParse(p);
            return d != null && d.NameToIndex.ContainsKey("slide") && d.NameToIndex.ContainsKey("hammer")
                && !d.NameToIndex.ContainsKey("hand_L");
        }

        void AutoFillOutput()
        {
            string bp = baseBox.Text.Trim();
            if (bp.Length > 0) outBox.Text = DefaultOutput(bp);
        }

        SmdDoc TryParse(string p)
        {
            try
            {
                if (!File.Exists(p)) return null;
                SmdDoc d = Parse(p);
                if (d.Nodes.Count == 0) return null;
                return d;
            }
            catch { return null; }
        }

        string SettingsPath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "smd_merge.ini"); }
        }

        void LoadSettings()
        {
            try
            {
                if (!File.Exists(SettingsPath)) return;
                string[] lines = File.ReadAllLines(SettingsPath);
                foreach (string raw in lines)
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim().ToLowerInvariant();
                    string v = line.Substring(eq + 1).Trim();
                    if (k == "base") { if (File.Exists(v)) baseBox.Text = v; }
                    else if (k == "mech") { if (File.Exists(v)) mechBox.Text = v; }
                    else if (k == "body") { if (File.Exists(v)) bodyBox.Text = v; }
                    else if (k == "scale") scaleBox.Text = v;
                    else if (k == "src2") { if (File.Exists(v)) srcBox.Text = v; }
                    else if (k == "tgt2") { if (File.Exists(v)) tgtBox.Text = v; }
                }
                if (baseBox.Text.Trim().Length > 0) Log("已恢复上次的文件与选项（可直接点开始拼接）");
            }
            catch { }
        }

        void SaveSettings()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("base=" + baseBox.Text.Trim());
                sb.AppendLine("mech=" + mechBox.Text.Trim());
                sb.AppendLine("body=" + bodyBox.Text.Trim());
                sb.AppendLine("scale=" + scaleBox.Text.Trim());
                sb.AppendLine("src2=" + srcBox.Text.Trim());
                sb.AppendLine("tgt2=" + tgtBox.Text.Trim());
                File.WriteAllText(SettingsPath, sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }
    }
}
