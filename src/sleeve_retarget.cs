using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

// 把动作源（Valve viewmodel 手臂 rig，如 1.smd）的手臂动作重定向到目标骨架
// （如 v_sleeve_ct.smd 的 Bip01 17 骨骼）上。只输出动画（nodes + skeleton），
// 骨骼位置沿用目标模型自带的静止值，旋转按「静止姿势坐标系」里的世界空间增量重定向。
//
// 增量：D = W_src(0)^T * W_src(f)，目标世界 = 目标静止 * D，再换算回局部。
public static class SleeveRetarget
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    class Node { public string Name; public int Parent; }
    class Bone { public double[] P = new double[3]; public double[] R = new double[3]; }

    static readonly Dictionary<string, string> ExactMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
        {"v_weapon.Bip01_L_UpperArm", "armUpperShoulder_L"},
        {"v_weapon.Bip01_L_Forearm",  "arm_lower_L"},
        {"v_weapon.Bip01_L_Hand",     "hand_L"},
        {"v_weapon.Bip01_L_ForeTwist","arm_lower_L"},
        {"v_weapon.Bip01_R_UpperArm", "armUpperShoulder_R"},
        {"v_weapon.Bip01_R_Forearm",  "arm_lower_R"},
        {"v_weapon.Bip01_R_Hand",     "hand_R"},
        {"v_weapon.Bip01_R_ForeTwist","arm_lower_R"},
    };

    public static void Run(string srcPath, string tgtPath, string outPath, Action<string> log)
    {
        if (log == null) log = delegate(string s) { };

        var srcLines = File.ReadAllLines(srcPath, Encoding.UTF8);
        var tgtLines = File.ReadAllLines(tgtPath, Encoding.UTF8);

        int srcSkel, tgtSkel;
        var srcNodes = ParseNodes(srcLines, out srcSkel);
        var tgtNodes = ParseNodes(tgtLines, out tgtSkel);
        if (srcSkel < 0 || tgtSkel < 0) throw new InvalidDataException("找不到 skeleton 段");

        var srcFrames = ParseFrames(srcLines, srcSkel, srcNodes.Count);
        var tgtFrames = ParseFrames(tgtLines, tgtSkel, tgtNodes.Count);
        log("动作源: " + Path.GetFileName(srcPath) + "  " + srcNodes.Count + " 骨骼, " + srcFrames.Count + " 帧");
        log("目标:   " + Path.GetFileName(tgtPath) + "  " + tgtNodes.Count + " 骨骼, " + tgtFrames.Count + " 帧");
        if (srcFrames.Count == 0 || tgtFrames.Count == 0) throw new InvalidDataException("缺少帧数据");

        var tgtRest = tgtFrames[0];
        var tgtRestWorld = ComputeWorlds(tgtNodes, tgtRest);

        var srcIdx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < srcNodes.Count; i++) srcIdx[srcNodes[i].Name] = i;
        var tgtIdx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < tgtNodes.Count; i++) tgtIdx[tgtNodes[i].Name] = i;

        // 目标骨骼 -> 源骨骼
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in tgtNodes)
        {
            string s = MapSource(n.Name);
            if (s != null && srcIdx.ContainsKey(s)) map[n.Name] = s;
        }
        if (map.Count == 0) throw new InvalidDataException("目标骨架里没有可识别的上臂/前臂/手骨骼（需要名字含 _L_/_R_ 的 UpperArm/Forearm/Hand）");

        var srcWorlds = new List<double[][,]>();
        foreach (var f in srcFrames) srcWorlds.Add(ComputeWorlds(srcNodes, f));
        var srcWorld0 = srcWorlds[0];

        int frameCount = srcFrames.Count;
        var sb = new StringBuilder();
        sb.AppendLine("version 1");
        sb.AppendLine("nodes");
        for (int i = 0; i < tgtNodes.Count; i++)
            sb.AppendLine("  " + i + " \"" + tgtNodes[i].Name + "\" " + tgtNodes[i].Parent);
        sb.AppendLine("end");
        sb.AppendLine("skeleton");

        var tgtWorlds = new List<double[][,]>();
        for (int f = 0; f < frameCount; f++)
        {
            var sw = srcWorlds[f];
            var tw = new double[tgtNodes.Count][,];
            sb.AppendLine("  time " + f);
            for (int i = 0; i < tgtNodes.Count; i++)
            {
                int p = tgtNodes[i].Parent;
                double[,] parentW = (p >= 0 && p < tgtNodes.Count) ? tw[p] : Identity();
                double[,] world;
                string srcName;
                if (map.TryGetValue(tgtNodes[i].Name, out srcName))
                {
                    int si = srcIdx[srcName];
                    var delta = Mul(Transpose(srcWorld0[si]), sw[si]);
                    world = Mul(tgtRestWorld[i], delta);
                }
                else
                {
                    world = Mul(parentW, EulerToMat(tgtRest[i].R[0], tgtRest[i].R[1], tgtRest[i].R[2]));
                }
                tw[i] = world;
                var local = Mul(Transpose(parentW), world);
                var e = MatToEuler(local);
                sb.AppendLine("    " + i + " " + F(tgtRest[i].P[0]) + " " + F(tgtRest[i].P[1]) + " " + F(tgtRest[i].P[2])
                    + " " + F(e[0]) + " " + F(e[1]) + " " + F(e[2]));
            }
            tgtWorlds.Add(tw);
        }
        sb.AppendLine("end");

        string dir = Path.GetDirectoryName(outPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(false));

        // 自检：每根映射骨骼的转动角度应与源一致
        var tgtW0 = tgtWorlds[0];
        double maxDev = 0;
        foreach (var kv in map)
        {
            int ti = tgtIdx[kv.Key];
            int si = srcIdx[kv.Value];
            for (int f = 0; f < frameCount; f++)
            {
                var dt = Mul(tgtWorlds[f][ti], Transpose(tgtW0[ti]));
                var ds = Mul(srcWorlds[f][si], Transpose(srcWorld0[si]));
                double dev = Math.Abs(AngleOf(dt) - AngleOf(ds));
                if (dev > maxDev) maxDev = dev;
            }
        }
        log("已映射骨骼: " + map.Count + " 根");
        log("重定向完成 → " + outPath + "  (" + frameCount + " 帧)");
        log("自检 max_angle_dev_rad=" + maxDev.ToString("0.000000", Inv));
    }

    static string MapSource(string tgtName)
    {
        string s;
        if (ExactMap.TryGetValue(tgtName, out s)) return s;
        string side = null;
        if (tgtName.IndexOf("_L_", StringComparison.OrdinalIgnoreCase) >= 0) side = "L";
        else if (tgtName.IndexOf("_R_", StringComparison.OrdinalIgnoreCase) >= 0) side = "R";
        if (side == null) return null;
        if (tgtName.EndsWith("_ForeTwist", StringComparison.OrdinalIgnoreCase)) return "arm_lower_" + side;
        if (tgtName.EndsWith("_Forearm", StringComparison.OrdinalIgnoreCase)) return "arm_lower_" + side;
        if (tgtName.EndsWith("_UpperArm", StringComparison.OrdinalIgnoreCase)) return "armUpperShoulder_" + side;
        if (tgtName.EndsWith("_Hand", StringComparison.OrdinalIgnoreCase)) return "hand_" + side;
        return null;
    }

    static string F(double v)
    {
        if (v == 0.0 && BitConverter.DoubleToInt64Bits(v) != 0L) return "-0.000000";
        return v.ToString("0.000000", Inv);
    }

    static double[,] Identity()
    {
        var m = new double[3, 3]; m[0, 0] = 1; m[1, 1] = 1; m[2, 2] = 1; return m;
    }

    // SMD 欧拉角约定 R = Rz(rz) * Ry(ry) * Rx(rx)
    static double[,] EulerToMat(double rx, double ry, double rz)
    {
        double cx = Math.Cos(rx), sx = Math.Sin(rx);
        double cy = Math.Cos(ry), sy = Math.Sin(ry);
        double cz = Math.Cos(rz), sz = Math.Sin(rz);
        var m = new double[3, 3];
        m[0, 0] = cz * cy;            m[0, 1] = cz * sy * sx - sz * cx;  m[0, 2] = cz * sy * cx + sz * sx;
        m[1, 0] = sz * cy;            m[1, 1] = sz * sy * sx + cz * cx;  m[1, 2] = sz * sy * cx - cz * sx;
        m[2, 0] = -sy;                m[2, 1] = cy * sx;                 m[2, 2] = cy * cx;
        return m;
    }

    static double[] MatToEuler(double[,] m)
    {
        double sy = -m[2, 0];
        if (sy > 1.0) sy = 1.0;
        if (sy < -1.0) sy = -1.0;
        double ry = Math.Asin(sy);
        double rx, rz;
        if (Math.Abs(m[2, 0]) < 0.9999999)
        {
            rx = Math.Atan2(m[2, 1], m[2, 2]);
            rz = Math.Atan2(m[1, 0], m[0, 0]);
        }
        else
        {
            rx = Math.Atan2(-m[1, 2], m[1, 1]);
            rz = 0.0;
        }
        return new double[] { rx, ry, rz };
    }

    static double[,] Mul(double[,] a, double[,] b)
    {
        var r = new double[3, 3];
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
            {
                double s = 0;
                for (int k = 0; k < 3; k++) s += a[i, k] * b[k, j];
                r[i, j] = s;
            }
        return r;
    }

    static double[,] Transpose(double[,] a)
    {
        var r = new double[3, 3];
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++) r[i, j] = a[j, i];
        return r;
    }

    static double AngleOf(double[,] m)
    {
        double tr = m[0, 0] + m[1, 1] + m[2, 2];
        double c = (tr - 1.0) / 2.0;
        if (c > 1.0) c = 1.0;
        if (c < -1.0) c = -1.0;
        return Math.Acos(c);
    }

    static List<Node> ParseNodes(string[] lines, out int skeletonLine)
    {
        var nodes = new List<Node>();
        bool inNodes = false;
        skeletonLine = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            string t = lines[i].Trim();
            if (t.Equals("nodes", StringComparison.OrdinalIgnoreCase)) { inNodes = true; continue; }
            if (inNodes && t.Equals("end", StringComparison.OrdinalIgnoreCase)) { inNodes = false; continue; }
            if (!inNodes)
            {
                if (t.Equals("skeleton", StringComparison.OrdinalIgnoreCase)) { skeletonLine = i; break; }
                continue;
            }
            int q1 = t.IndexOf('"');
            int q2 = t.LastIndexOf('"');
            if (q1 < 0 || q2 <= q1) continue;
            string name = t.Substring(q1 + 1, q2 - q1 - 1);
            string tail = t.Substring(q2 + 1).Trim();
            int parent;
            if (!int.TryParse(tail, NumberStyles.Integer, Inv, out parent)) continue;
            nodes.Add(new Node { Name = name, Parent = parent });
        }
        return nodes;
    }

    static List<Bone[]> ParseFrames(string[] lines, int skeletonLine, int nodeCount)
    {
        var frames = new List<Bone[]>();
        Bone[] cur = null;
        for (int i = skeletonLine + 1; i < lines.Length; i++)
        {
            string t = lines[i].Trim();
            if (t.Equals("end", StringComparison.OrdinalIgnoreCase)) break;
            if (t.StartsWith("time ", StringComparison.OrdinalIgnoreCase))
            {
                cur = new Bone[nodeCount];
                for (int k = 0; k < nodeCount; k++) cur[k] = new Bone();
                frames.Add(cur);
                continue;
            }
            if (cur == null) continue;
            var p = t.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (p.Length != 7) continue;
            int idx;
            if (!int.TryParse(p[0], NumberStyles.Integer, Inv, out idx)) continue;
            if (idx < 0 || idx >= nodeCount) continue;
            cur[idx].P[0] = double.Parse(p[1], Inv);
            cur[idx].P[1] = double.Parse(p[2], Inv);
            cur[idx].P[2] = double.Parse(p[3], Inv);
            cur[idx].R[0] = double.Parse(p[4], Inv);
            cur[idx].R[1] = double.Parse(p[5], Inv);
            cur[idx].R[2] = double.Parse(p[6], Inv);
        }
        return frames;
    }

    static double[][,] ComputeWorlds(List<Node> nodes, Bone[] frame)
    {
        var w = new double[nodes.Count][,];
        for (int i = 0; i < nodes.Count; i++)
        {
            var local = EulerToMat(frame[i].R[0], frame[i].R[1], frame[i].R[2]);
            int p = nodes[i].Parent;
            w[i] = (p >= 0 && p < nodes.Count) ? Mul(w[p], local) : local;
        }
        return w;
    }
}
