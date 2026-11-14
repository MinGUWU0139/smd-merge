using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

// 把 1.smd（Valve viewmodel 手臂 rig）的动作重定向到 v_sleeve_ct.smd 的 Bip01 17 骨骼上。
// 只输出动画（nodes + skeleton），骨骼位置沿用袖子模型自带的静止值，旋转按世界空间增量重定向。
class SleeveRetarget
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    class Node { public string Name; public int Parent; }
    class Bone { public double[] P = new double[3]; public double[] R = new double[3]; }

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

    static void Main(string[] args)
    {
        if (args.Length < 3)
        {
            Console.WriteLine("usage: sleeve_retarget <source.smd> <target_rest.smd> <out.smd>");
            Environment.Exit(2);
        }
        string srcPath = args[0], tgtPath = args[1], outPath = args[2];

        var srcLines = File.ReadAllLines(srcPath, Encoding.UTF8);
        var tgtLines = File.ReadAllLines(tgtPath, Encoding.UTF8);

        int srcSkel, tgtSkel;
        var srcNodes = ParseNodes(srcLines, out srcSkel);
        var tgtNodes = ParseNodes(tgtLines, out tgtSkel);
        if (srcSkel < 0 || tgtSkel < 0) { Console.WriteLine("error: no skeleton section"); Environment.Exit(1); }

        var srcFrames = ParseFrames(srcLines, srcSkel, srcNodes.Count);
        var tgtFrames = ParseFrames(tgtLines, tgtSkel, tgtNodes.Count);
        Console.WriteLine("source: " + srcNodes.Count + " nodes, " + srcFrames.Count + " frames");
        Console.WriteLine("target: " + tgtNodes.Count + " nodes, " + tgtFrames.Count + " frames");

        var tgtRest = tgtFrames[0];
        var tgtRestWorld = ComputeWorlds(tgtNodes, tgtRest);

        var srcIdx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < srcNodes.Count; i++) srcIdx[srcNodes[i].Name] = i;
        var tgtIdx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < tgtNodes.Count; i++) tgtIdx[tgtNodes[i].Name] = i;

        // 目标骨骼 -> 源骨骼
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
            {"v_weapon.Bip01_L_UpperArm", "armUpperShoulder_L"},
            {"v_weapon.Bip01_L_Forearm",  "arm_lower_L"},
            {"v_weapon.Bip01_L_Hand",     "hand_L"},
            {"v_weapon.Bip01_L_ForeTwist","arm_lower_L"},
            {"v_weapon.Bip01_R_UpperArm", "armUpperShoulder_R"},
            {"v_weapon.Bip01_R_Forearm",  "arm_lower_R"},
            {"v_weapon.Bip01_R_Hand",     "hand_R"},
            {"v_weapon.Bip01_R_ForeTwist","arm_lower_R"},
        };

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
                    int si;
                    if (srcIdx.TryGetValue(srcName, out si))
                    {
                        // 静止姿势坐标系里的增量：D = W_src(0)^T * W_src(f)
                        // 两套骨架静止朝向的差异（源带 root_motion -90°）会被自然消除。
                        var delta = Mul(Transpose(srcWorld0[si]), sw[si]);
                        world = Mul(tgtRestWorld[i], delta);
                    }
                    else
                    {
                        world = Mul(parentW, EulerToMat(tgtRest[i].R[0], tgtRest[i].R[1], tgtRest[i].R[2]));
                    }
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

        File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(false));
        Console.WriteLine("written: " + outPath + "  frames=" + frameCount);

        var tgtW0 = tgtWorlds[0];
        double maxDev = 0;
        foreach (var kv in map)
        {
            int ti; int si2;
            if (!tgtIdx.TryGetValue(kv.Key, out ti)) continue;
            if (!srcIdx.TryGetValue(kv.Value, out si2)) continue;
            for (int f = 0; f < frameCount; f++)
            {
                var dt = Mul(tgtWorlds[f][ti], Transpose(tgtW0[ti]));
                var ds = Mul(srcWorlds[f][si2], Transpose(srcWorld0[si2]));
                double dev = Math.Abs(AngleOf(dt) - AngleOf(ds));
                if (dev > maxDev) maxDev = dev;
            }
        }
        Console.WriteLine("max_angle_dev_rad=" + maxDev.ToString("0.000000", Inv));
    }
}
