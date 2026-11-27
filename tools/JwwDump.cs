// Console check of the C# JWW reader (no Rhino needed): JwwDump.exe file.jww [...]
// Build: see build.ps1 -Tools
using System;
using System.Collections.Generic;
using Jww;

class JwwDump
{
    static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        int bad = 0;
        foreach (var p in args)
        {
            try
            {
                JwwDocument d = JwwReader.Read(p);
                var kinds = new SortedDictionary<string, int>();
                foreach (var e in d.Entities) { string k = e.GetType().Name; int n; kinds.TryGetValue(k, out n); kinds[k] = n + 1; }
                int members = 0; foreach (var b in d.Blocks.Values) members += b.Members.Count;
                Console.WriteLine("{0}", p);
                Console.WriteLine("  version {0}  paper {1}  scale[0] 1/{2}  entities {3}  blocks {4} ({5} members)",
                    d.Version, JwwDocument.PaperName(d.Paper), d.GroupScale[0], d.Entities.Count, d.Blocks.Count, members);
                foreach (var kv in kinds) Console.WriteLine("    {0,-12} {1}", kv.Key, kv.Value);
                Console.WriteLine("  data ends at {0} of {1} bytes", d.EndOfData, d.FileLength);
                foreach (var w in d.Warnings) Console.WriteLine("  WARNING " + w);
                int shown = 0;
                foreach (var e in d.Entities)
                {
                    var t = e as JwText;
                    if (t != null && shown++ < 3) Console.WriteLine("    text '{0}' at ({1:0.0},{2:0.0}) size {3} font {4} grp {5}", t.Value, t.X1, t.Y1, t.SizeY, t.Font, t.Group);
                }
            }
            catch (Exception ex) { bad++; Console.WriteLine("{0}\n  ERROR {1}", p, ex.Message); }
        }
        return bad;
    }
}
