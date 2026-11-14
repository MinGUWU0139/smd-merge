using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

// SMD animation auto-merge tool.
//
// Overlays bone animation from a Blender re-export (source) onto a full
// Source-engine skeleton (target) by matching bone names, with the
// RotZ(+90 deg) frame conversion from the Blender "blender_implicit" world
// frame to the Source "weapon_offset" frame.
//
// Build on Windows without Visual Studio (uses the in-box .NET Framework csc):
//   %WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:exe /out:smd_merge.exe smd_merge.cs

class SmdMerge
{
    class Node
    {
        public int Index;
        public string Name;
        public int Parent;
    }

    class Bone
    {
        public int Index;
        public double Px, Py, Pz;
        public double Rx, Ry, Rz;
    }

    class SmdDoc
    {
        public List<Node> Nodes = new List<Node>();
        public List<Bone[]> Frames = new List<Bone[]>();   // each frame: array indexed by bone index
        public Dictionary<string, int> NameToIndex = new Dictionary<string, int>();
        public int BoneCount;
    }

    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    const double HALF_PI = Math.PI / 2.0;
    static bool NoPause = false;

    static int Main(string[] args)
    {
        bool noRotate = false;
        List<string> files = new List<string>();
        foreach (string a in args)
        {
            string t = a.Trim();
            if (t == "--no-pause")
                NoPause = true;
            else if (t == "--no-rotate" || t == "--norotate" || t == "-n")
                noRotate = true;
            else if (t.Length > 0)
                files.Add(t);
        }

        if (files.Count == 0)
        {
            PrintUsage();
            Console.WriteLine("--- interactive mode ---");
            Console.Write("source .smd (Blender mechanism anim): ");
            string s = Console.ReadLine();
            Console.Write("target .smd (full skeleton anim): ");
            string t2 = Console.ReadLine();
            Console.Write("output .smd (default = next to target): ");
            string o = Console.ReadLine();
            if (string.IsNullOrEmpty(s) || string.IsNullOrEmpty(t2))
            {
                Console.WriteLine("cancelled.");
                Pause();
                return 1;
            }
            if (string.IsNullOrEmpty(o)) o = DefaultOutput(t2);
            files.Add(s); files.Add(t2); files.Add(o);
        }
        else if (files.Count == 2)
        {
            files.Add(DefaultOutput(files[1]));
        }
        else if (files.Count != 3)
        {
            PrintUsage();
            Pause();
            return 1;
        }

        string srcPath = files[0];
        string tgtPath = files[1];
        string outPath = files[2];

        try
        {
            Console.WriteLine("read source: " + srcPath);
            SmdDoc src = Parse(srcPath);
            Console.WriteLine("  source: " + src.Nodes.Count + " bones, " + src.Frames.Count + " frames");

            Console.WriteLine("read target: " + tgtPath);
            SmdDoc tgt = Parse(tgtPath);
            Console.WriteLine("  target: " + tgt.Nodes.Count + " bones, " + tgt.Frames.Count + " frames");

            int frames = Math.Min(src.Frames.Count, tgt.Frames.Count);
            if (src.Frames.Count != tgt.Frames.Count)
                Console.WriteLine("WARNING: frame counts differ (" + src.Frames.Count + " vs " + tgt.Frames.Count + "); using " + frames + ".");

            List<string> shared = new List<string>();
            foreach (KeyValuePair<string, int> kv in src.NameToIndex)
            {
                if (kv.Key == "blender_implicit") continue;   // skip the implicit root
                if (tgt.NameToIndex.ContainsKey(kv.Key)) shared.Add(kv.Key);
            }
            if (shared.Count == 0)
            {
                Console.WriteLine("ERROR: no bone names shared between the two files.");
                Pause();
                return 1;
            }

            List<Bone[]> outFrames = new List<Bone[]>();
            for (int f = 0; f < frames; f++)
            {
                Bone[] sframe = src.Frames[f];
                Bone[] tframe = tgt.Frames[f];
                Bone[] oframe = new Bone[tgt.BoneCount];
                for (int k = 0; k < tgt.BoneCount; k++)
                {
                    Bone tb = tframe[k];
                    if (tb == null) continue;
                    Bone nb = new Bone();
                    nb.Index = tb.Index;
                    nb.Px = tb.Px; nb.Py = tb.Py; nb.Pz = tb.Pz;
                    nb.Rx = tb.Rx; nb.Ry = tb.Ry; nb.Rz = tb.Rz;
                    oframe[k] = nb;
                }
                foreach (string name in shared)
                {
                    int si = src.NameToIndex[name];
                    int ti = tgt.NameToIndex[name];
                    Bone sb = sframe[si];
                    Bone ob = oframe[ti];
                    if (sb == null || ob == null) continue;
                    double px = sb.Px, py = sb.Py, pz = sb.Pz;
                    double rx = sb.Rx, ry = sb.Ry, rz = sb.Rz;
                    if (!noRotate) Convert(ref px, ref py, ref pz, ref rx, ref ry, ref rz);
                    ob.Px = px; ob.Py = py; ob.Pz = pz;
                    ob.Rx = rx; ob.Ry = ry; ob.Rz = rz;
                }
                outFrames.Add(oframe);
            }

            string dir = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            Write(tgt, outFrames, outPath);

            Console.WriteLine("merged -> " + outPath);
            Console.WriteLine("  frames: " + frames);
            Console.WriteLine("  overlaid bones: " + string.Join(", ", shared.ToArray()) + (noRotate ? "" : "  (with RotZ+90deg conversion)"));
            Console.WriteLine("done.");
            Pause();
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("ERROR: " + ex.Message);
            Pause();
            return 1;
        }
    }

    static void Pause()
    {
        if (NoPause) return;
        Console.WriteLine("press any key to exit...");
        try { Console.ReadKey(); } catch { }
    }

    // RotZ(+90 deg) frame conversion: Blender "blender_implicit" world frame
    // -> Source "weapon_offset" frame.
    static void Convert(ref double px, ref double py, ref double pz, ref double rx, ref double ry, ref double rz)
    {
        // position (x,y,z) -> (-y, x, z)
        double ox = px, oy = py;
        px = -oy;
        py = ox;
        // SMD rotation is R = Rz(rz)*Ry(ry)*Rx(rx); left-multiplying by RotZ(+90)
        // simply adds +90 deg to rz.
        rz += HALF_PI;
    }

    static string DefaultOutput(string tgtPath)
    {
        string dir = Path.GetDirectoryName(tgtPath);
        string name = Path.GetFileNameWithoutExtension(tgtPath) + "_merged.smd";
        return string.IsNullOrEmpty(dir) ? name : Path.Combine(dir, name);
    }

    static void PrintUsage()
    {
        Console.WriteLine("SMD animation auto-merge");
        Console.WriteLine("usage:");
        Console.WriteLine("  smd_merge.exe <source.smd> <target.smd> <output.smd> [--no-rotate]");
        Console.WriteLine("  smd_merge.exe <source.smd> <target.smd>            (output auto-named)");
        Console.WriteLine("  smd_merge.exe                                       (interactive)");
        Console.WriteLine();
        Console.WriteLine("  source : Blender re-export providing the mechanism animation (e.g. 1.smd)");
        Console.WriteLine("  target : full skeleton animation to be overlaid (e.g. original reload)");
        Console.WriteLine("  --no-rotate : skip the RotZ(+90 deg) frame conversion (same-frame files)");
        Console.WriteLine();
        Console.WriteLine("bones are matched by name; source bones overwrite same-named target");
        Console.WriteLine("bones frame by frame. default conversion (Blender 'blender_implicit' world");
        Console.WriteLine("frame -> Source 'weapon_offset' frame):");
        Console.WriteLine("  position (x,y,z) -> (-y, x, z)");
        Console.WriteLine("  rotation (rx,ry,rz) -> (rx, ry, rz + pi/2)");
    }

    // ---------------- parsing ----------------

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
        for (int k = 0; k < doc.Nodes.Count; k++)
        {
            Node n = doc.Nodes[k];
            if (!doc.NameToIndex.ContainsKey(n.Name)) doc.NameToIndex[n.Name] = n.Index;
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
        return b;
    }

    // ---------------- writing ----------------

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
                outLines.Add(b.Index + "  " + F(b.Px) + " " + F(b.Py) + " " + F(b.Pz) + "  " + F(b.Rx) + " " + F(b.Ry) + " " + F(b.Rz));
            }
        }
        outLines.Add("end");
        File.WriteAllLines(outPath, outLines.ToArray(), new UTF8Encoding(false));
    }
}
