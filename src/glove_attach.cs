using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

// 手臂合并（对齐绑定）：
//   把「手套/袖子」模型（如 v_glove_anarchist.smd）的骨骼挂到「目标骨架」（如 anarchist_test.smd）下，
//   输出使用手套那套网格；align=true 时按逐骨骼世界矩阵增量把手套网格重定向到目标骨架的姿势。
public static class GloveAttach
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    class Node
    {
        public string Name;
        public int Parent;
        public double Px, Py, Pz, Rx, Ry, Rz;
        public double[][] W;
        public double[] WT;
    }

    class Smd
    {
        public List<Node> Nodes = new List<Node>();
        public List<string> TriLines = new List<string>();
    }

    public static void Run(string glovePath, string testPath, string outPath, bool align, Action<string> log)
    {
        log("读取手套/袖子模型: " + glovePath);
        var glove = Parse(glovePath, true);
        log("  手套: " + glove.Nodes.Count + " 骨骼");
        log("读取目标骨架: " + testPath);
        var test = Parse(testPath, false);
        log("  目标: " + test.Nodes.Count + " 骨骼");

        ComputeWorlds(glove.Nodes);
        ComputeWorlds(test.Nodes);

        var testIndexByName = new Dictionary<string, int>();
        for (int i = 0; i < test.Nodes.Count; i++) testIndexByName[test.Nodes[i].Name] = i;

        // 保存手套原始世界矩阵（逐骨骼重定向用）
        var gOrigW = new double[glove.Nodes.Count][][];
        var gOrigWT = new double[glove.Nodes.Count][];
        for (int i = 0; i < glove.Nodes.Count; i++) { gOrigW[i] = glove.Nodes[i].W; gOrigWT[i] = glove.Nodes[i].WT; }
        log("模式: " + (align ? "逐骨骼重定向对齐（网格跟着目标骨架姿势）" : "只挂父级（保持手套原姿势）"));

        // 输出节点表：目标全部 + 手套独有的 14 根
        var outNodes = new List<Node>();
        for (int i = 0; i < test.Nodes.Count; i++)
        {
            var t = test.Nodes[i];
            var n = new Node();
            n.Name = t.Name; n.Parent = t.Parent;
            n.Px = t.Px; n.Py = t.Py; n.Pz = t.Pz;
            n.Rx = t.Rx; n.Ry = t.Ry; n.Rz = t.Rz;
            outNodes.Add(n);
        }

        var adds = new List<KeyValuePair<int, string>>();
        adds.Add(new KeyValuePair<int, string>(0, "root_motion"));
        adds.Add(new KeyValuePair<int, string>(1, "__PREV__"));
        adds.Add(new KeyValuePair<int, string>(2, "__PREV__"));
        adds.Add(new KeyValuePair<int, string>(3, "__PREV__"));
        adds.Add(new KeyValuePair<int, string>(4, "__PREV__"));
        adds.Add(new KeyValuePair<int, string>(5, "__PREV__"));
        adds.Add(new KeyValuePair<int, string>(6, "__PREV__"));
        adds.Add(new KeyValuePair<int, string>(7, "__PREV__"));
        adds.Add(new KeyValuePair<int, string>(8, "__PREV__"));
        adds.Add(new KeyValuePair<int, string>(9, "armUpperShoulder_L"));
        adds.Add(new KeyValuePair<int, string>(10, "arm_lower_L"));
        adds.Add(new KeyValuePair<int, string>(28, "__PREV__"));
        adds.Add(new KeyValuePair<int, string>(29, "armUpperShoulder_R"));
        adds.Add(new KeyValuePair<int, string>(30, "arm_lower_R"));

        int neckOutIndex = -1;
        for (int k = 0; k < adds.Count; k++)
        {
            var g = glove.Nodes[adds[k].Key];
            var n = new Node();
            n.Name = g.Name;
            if (adds[k].Value == "__PREV__") n.Parent = outNodes.Count - 1;
            else n.Parent = testIndexByName[adds[k].Value];
            outNodes.Add(n);
            if (g.Name == "v_weapon.Bip01_Neck") neckOutIndex = outNodes.Count - 1;
        }
        for (int k = 0; k < adds.Count; k++)
            if (glove.Nodes[adds[k].Key].Name == "v_weapon.Bip01_R_Clavicle")
                outNodes[test.Nodes.Count + k].Parent = neckOutIndex;

        var outIndexByName = new Dictionary<string, int>();
        for (int i = 0; i < outNodes.Count; i++) outIndexByName[outNodes[i].Name] = i;

        var testWorldR = new double[test.Nodes.Count][][];
        var testWorldT = new double[test.Nodes.Count][];
        for (int i = 0; i < test.Nodes.Count; i++) { testWorldR[i] = test.Nodes[i].W; testWorldT[i] = test.Nodes[i].WT; }

        var addedNames = new HashSet<string>();
        for (int k = 0; k < adds.Count; k++) addedNames.Add(glove.Nodes[adds[k].Key].Name);

        // 同名骨骼：输出世界 = 目标世界，再按父节点反算 local
        int sharedCount = 0;
        for (int i = 0; i < glove.Nodes.Count; i++)
        {
            int oi;
            if (!outIndexByName.TryGetValue(glove.Nodes[i].Name, out oi)) continue;
            if (addedNames.Contains(glove.Nodes[i].Name)) continue;
            int parent = outNodes[oi].Parent;
            int ti2 = testIndexByName[glove.Nodes[i].Name];
            outNodes[oi].W = test.Nodes[ti2].W; outNodes[oi].WT = test.Nodes[ti2].WT;
            SetLocalFromWorld(outNodes[oi], testWorldR[parent], testWorldT[parent]);
            sharedCount++;
        }

        // 追加的 14 根：28/29/30 映射到目标右侧手臂骨骼，其余保持手套世界
        for (int k = 0; k < adds.Count; k++)
        {
            int oi = test.Nodes.Count + k;
            int gIdx = adds[k].Key;
            var gw = glove.Nodes[gIdx];
            string mapped = null;
            if (gIdx == 29) mapped = "armUpperShoulder_R";
            else if (gIdx == 30) mapped = "arm_lower_R";
            else if (gIdx == 28) mapped = "armUpperShoulder_R";
            if (mapped != null && testIndexByName.ContainsKey(mapped))
            {
                int ti3 = testIndexByName[mapped];
                outNodes[oi].W = test.Nodes[ti3].W; outNodes[oi].WT = test.Nodes[ti3].WT;
            }
            else
            {
                outNodes[oi].W = gw.W; outNodes[oi].WT = gw.WT;
            }
            int parent = outNodes[oi].Parent;
            double[][] pr; double[] pt;
            if (parent < test.Nodes.Count) { pr = testWorldR[parent]; pt = testWorldT[parent]; }
            else { pr = outNodes[parent].W; pt = outNodes[parent].WT; }
            SetLocalFromWorld(outNodes[oi], pr, pt);
        }
        log("  同名骨骼: " + sharedCount + " 根，追加: " + adds.Count + " 根，合计: " + outNodes.Count + " 根");

        var sb = new StringBuilder();
        sb.AppendLine("version 1");
        sb.AppendLine("nodes");
        for (int i = 0; i < outNodes.Count; i++)
            sb.AppendLine(i + " \"" + outNodes[i].Name + "\" " + outNodes[i].Parent);
        sb.AppendLine("end");
        sb.AppendLine("skeleton");
        sb.AppendLine("time 0");
        for (int i = 0; i < outNodes.Count; i++)
        {
            var n = outNodes[i];
            sb.AppendLine(i + "  " + F(n.Px) + " " + F(n.Py) + " " + F(n.Pz) + "  " + F(n.Rx) + " " + F(n.Ry) + " " + F(n.Rz));
        }
        sb.AppendLine("end");

        var map = new int[glove.Nodes.Count];
        for (int i = 0; i < glove.Nodes.Count; i++) map[i] = outIndexByName[glove.Nodes[i].Name];

        sb.AppendLine("triangles");
        int vertCount = 0;
        for (int k = 1; k < glove.TriLines.Count; k++)
        {
            string line = glove.TriLines[k];
            if (line.Trim() == "end") { break; }
            string[] p = line.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (p.Length >= 10 && (char.IsDigit(p[0][0]) || p[0][0] == '-'))
            {
                int bone = int.Parse(p[0], Inv);
                sb.Append(map[bone].ToString(Inv));
                if (align)
                {
                    var pos = new double[] { double.Parse(p[1], Inv), double.Parse(p[2], Inv), double.Parse(p[3], Inv) };
                    var nor = new double[] { double.Parse(p[4], Inv), double.Parse(p[5], Inv), double.Parse(p[6], Inv) };
                    int oi2 = map[bone];
                    var Rold = gOrigW[bone]; var told = gOrigWT[bone];
                    var Rnew = outNodes[oi2].W; var tnew = outNodes[oi2].WT;
                    double vx = pos[0] - told[0], vy = pos[1] - told[1], vz = pos[2] - told[2];
                    double ux = Rold[0][0] * vx + Rold[1][0] * vy + Rold[2][0] * vz;
                    double uy = Rold[0][1] * vx + Rold[1][1] * vy + Rold[2][1] * vz;
                    double uz = Rold[0][2] * vx + Rold[1][2] * vy + Rold[2][2] * vz;
                    var np = new double[3];
                    for (int a2 = 0; a2 < 3; a2++) np[a2] = Rnew[a2][0] * ux + Rnew[a2][1] * uy + Rnew[a2][2] * uz + tnew[a2];
                    double nx = nor[0], ny = nor[1], nz = nor[2];
                    double unx = Rold[0][0] * nx + Rold[1][0] * ny + Rold[2][0] * nz;
                    double uny = Rold[0][1] * nx + Rold[1][1] * ny + Rold[2][1] * nz;
                    double unz = Rold[0][2] * nx + Rold[1][2] * ny + Rold[2][2] * nz;
                    var nn = new double[3];
                    for (int a2 = 0; a2 < 3; a2++) nn[a2] = Rnew[a2][0] * unx + Rnew[a2][1] * uny + Rnew[a2][2] * unz;
                    sb.Append(' '); sb.Append(F(np[0]));
                    sb.Append(' '); sb.Append(F(np[1]));
                    sb.Append(' '); sb.Append(F(np[2]));
                    sb.Append(' '); sb.Append(F(nn[0]));
                    sb.Append(' '); sb.Append(F(nn[1]));
                    sb.Append(' '); sb.Append(F(nn[2]));
                    sb.Append(' '); sb.Append(p[7]);
                    sb.Append(' '); sb.Append(p[8]);
                }
                else
                {
                    for (int i = 1; i <= 8; i++) { sb.Append(' '); sb.Append(p[i]); }
                }
                int links = int.Parse(p[9], Inv);
                sb.Append(' '); sb.Append(links.ToString(Inv));
                int idx = 10;
                for (int i = 0; i < links; i++)
                {
                    int b = int.Parse(p[idx], Inv);
                    sb.Append(' '); sb.Append(map[b].ToString(Inv));
                    sb.Append(' '); sb.Append(p[idx + 1]);
                    idx += 2;
                }
                sb.AppendLine();
                vertCount++;
            }
            else
            {
                sb.AppendLine(line);
            }
        }
        sb.AppendLine("end");

        File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(false));
        log("  输出: 节点 " + outNodes.Count + " / 顶点 " + vertCount + " / " + new FileInfo(outPath).Length + " 字节");
        log("完成: " + outPath);
    }

    // ---------------- 解析 ----------------

    static Smd Parse(string path, bool wantTri)
    {
        string[] L = File.ReadAllLines(path, Encoding.UTF8);
        var smd = new Smd();
        int i = 0;
        while (i < L.Length && L[i].Trim() != "nodes") i++;
        i++;
        while (i < L.Length && L[i].Trim() != "end")
        {
            string t = L[i].Trim();
            int q1 = t.IndexOf('"'), q2 = t.LastIndexOf('"');
            if (q1 >= 0 && q2 > q1)
            {
                var n = new Node();
                n.Name = t.Substring(q1 + 1, q2 - q1 - 1);
                n.Parent = int.Parse(t.Substring(q2 + 1).Trim(), Inv);
                smd.Nodes.Add(n);
            }
            i++;
        }
        while (i < L.Length && L[i].Trim() != "skeleton") i++;
        i++;
        if (i < L.Length && L[i].Trim().StartsWith("time ")) i++;
        while (i < L.Length && L[i].Trim() != "end")
        {
            string t = L[i].Trim();
            if (t.Length > 0)
            {
                string[] p = t.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (p.Length >= 7)
                {
                    int idx = int.Parse(p[0], Inv);
                    if (idx >= 0 && idx < smd.Nodes.Count)
                    {
                        var n = smd.Nodes[idx];
                        n.Px = double.Parse(p[1], Inv); n.Py = double.Parse(p[2], Inv); n.Pz = double.Parse(p[3], Inv);
                        n.Rx = double.Parse(p[4], Inv); n.Ry = double.Parse(p[5], Inv); n.Rz = double.Parse(p[6], Inv);
                    }
                }
            }
            i++;
        }
        if (wantTri)
        {
            while (i < L.Length && L[i].Trim() != "triangles") i++;
            for (; i < L.Length; i++)
            {
                smd.TriLines.Add(L[i]);
                if (L[i].Trim() == "end" && smd.TriLines.Count > 1) break;
            }
        }
        return smd;
    }

    // ---------------- 矩阵 ----------------

    static double[][] Mul(double[][] a, double[][] b)
    {
        var r = new double[3][];
        for (int i = 0; i < 3; i++) r[i] = new double[3];
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
            {
                double s = 0;
                for (int k = 0; k < 3; k++) s += a[i][k] * b[k][j];
                r[i][j] = s;
            }
        return r;
    }

    static double[][] Transpose(double[][] a)
    {
        var r = new double[3][];
        for (int i = 0; i < 3; i++) r[i] = new double[3];
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++) r[i][j] = a[j][i];
        return r;
    }

    static double[][] EulerToMat(double rx, double ry, double rz)
    {
        double cx = Math.Cos(rx), sx = Math.Sin(rx);
        double cy = Math.Cos(ry), sy = Math.Sin(ry);
        double cz = Math.Cos(rz), sz = Math.Sin(rz);
        var Rx = new double[][] { new double[] { 1, 0, 0 }, new double[] { 0, cx, -sx }, new double[] { 0, sx, cx } };
        var Ry = new double[][] { new double[] { cy, 0, sy }, new double[] { 0, 1, 0 }, new double[] { -sy, 0, cy } };
        var Rz = new double[][] { new double[] { cz, -sz, 0 }, new double[] { sz, cz, 0 }, new double[] { 0, 0, 1 } };
        return Mul(Mul(Rz, Ry), Rx);
    }

    static void MatToEuler(double[][] R, out double rx, out double ry, out double rz)
    {
        double sy = -R[2][0];
        if (sy > 1) sy = 1; if (sy < -1) sy = -1;
        ry = Math.Asin(sy);
        rx = Math.Atan2(R[2][1], R[2][2]);
        rz = Math.Atan2(R[1][0], R[0][0]);
    }

    static void ComputeWorlds(List<Node> nodes)
    {
        for (int i = 0; i < nodes.Count; i++)
        {
            var n = nodes[i];
            var Lm = EulerToMat(n.Rx, n.Ry, n.Rz);
            var t = new double[] { n.Px, n.Py, n.Pz };
            if (n.Parent < 0 || n.Parent >= nodes.Count)
            {
                n.W = Lm; n.WT = t;
            }
            else
            {
                var pr = nodes[n.Parent].W; var pt = nodes[n.Parent].WT;
                n.W = Mul(pr, Lm);
                var np = new double[3];
                for (int a = 0; a < 3; a++)
                    np[a] = pt[a] + pr[a][0] * t[0] + pr[a][1] * t[1] + pr[a][2] * t[2];
                n.WT = np;
            }
        }
    }

    static void SetLocalFromWorld(Node n, double[][] Wparent, double[] Tparent)
    {
        var Rl = Mul(Transpose(Wparent), n.W);
        var d = new double[3];
        for (int a = 0; a < 3; a++) d[a] = n.WT[a] - Tparent[a];
        var tl = new double[3];
        for (int a = 0; a < 3; a++)
            tl[a] = Wparent[0][a] * d[0] + Wparent[1][a] * d[1] + Wparent[2][a] * d[2];
        n.Px = tl[0]; n.Py = tl[1]; n.Pz = tl[2];
        double rx, ry, rz;
        MatToEuler(Rl, out rx, out ry, out rz);
        n.Rx = rx; n.Ry = ry; n.Rz = rz;
    }

    static string F(double v)
    {
        return v.ToString("0.000000", Inv);
    }
}
