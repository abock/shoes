// 
// Shoes.cs
//  
// Author:
//       Aaron Bockover <abockover@novell.com>
// 
// Copyright 2009 Aaron Bockover
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Xml;
using System.Xml.XPath;
using System.Text.RegularExpressions;
using Mono.Options;

public static class Shoes
{
    private static List<string> search_paths = new List<string> ();
    private static List<Assembly> assemblies = new List<Assembly> ();
    private static List<string> blacklist = new List<string> ();

    private static List<string> native_files = new List<string> ();
    private static List<string> managed_files = new List<string> ();
    private static List<string> debug_files = new List<string> ();
    private static List<string> misc_files = new List<string> ();

    private static string strip_dir = Path.Combine (Environment.CurrentDirectory, ".stripped-shoes");

    private class BlacklistComparer : IEqualityComparer<string>
    {
        public bool Equals (string a, string b) { return Path.GetFileName (b).StartsWith (a); }
        public int GetHashCode (string o) { return o.GetHashCode (); }
    }
    
    public static void Main (string [] args)
    {
        string mono_binary = "/usr/bin/mono";
        bool show_help = false;
        bool strip_binaries = true;
        bool include_mdb = false;
        string blacklist_file = null;

        var p = new OptionSet () {
            { "m|mono=", "which mono binary to use (/usr/bin/mono)", v => mono_binary = v },
            { "s|strip", "strip native binaries of debug symbols", v => strip_binaries = v != null },
            { "d|mdb", "include mdb files in summary", v => include_mdb = v != null },
            { "b|blacklist=", "blacklist file to exclude native libraries from the summary", v => blacklist_file = v },
            { "h|help", "show this message and exit", v => show_help = v != null }
        };

        List<string> paths;
        try {
            paths = p.Parse (args);
        } catch (OptionException e) {
            Console.Write ("Shoes: ");
            Console.WriteLine (e.Message);
            Console.WriteLine ("Try --help for more information");
            return;
        }

        if (show_help) {
            Console.WriteLine ("Usage: Shoes [OPTIONS]+ <managed_dir1> <managed_dir2>...");
            Console.WriteLine ();
            Console.WriteLine ("Options:");
            p.WriteOptionDescriptions (Console.Out);
            return;
        }

        Console.WriteLine ("Shoes Configuration:");

        foreach (var env in new string [] { "LD_LIBRARY_PATH", "MONO_PATH" }) {
            var val = Environment.GetEnvironmentVariable (env);
            if (!String.IsNullOrEmpty (val)) {
                Console.WriteLine ("  $ export {0}={1}", env, val);
            }
        }
        
        Console.Write ("  $ mono Shoes.exe ");
        if (blacklist_file != null) {
            Console.Write ("--blacklist={0} ", blacklist_file);
        }
        if (!strip_binaries) {
            Console.Write ("--strip- ");
        }
        if (include_mdb) {
            Console.Write ("--mdb ");
        }
        foreach (var path in paths) {
            Console.Write ("{0} ", path);
        }
        Console.WriteLine ();
        Console.WriteLine ();

        if (paths.Count == 0) {
            Console.WriteLine ("Error: no paths to shoealyze");
            return;
        }

        FindNativeLibrarySearchPaths ();
        foreach (var path in paths) {
            Walk (path);
        }

        if (assemblies.Count > 0) {
            Walk (mono_binary);
        }

        foreach (var asm in assemblies) {
            managed_files.Add (asm.Location);
        }

        long total_size = 0;
        var labels = new string [] { 
            "Managed Code",
            "Native Code" + (strip_binaries ? " (stripped)" : String.Empty),
            "Debugging Symbols",
            "Misc Data"
        };
        var items = new List<List<string>> () { managed_files, native_files, debug_files, misc_files };
        var sizes = new long[4];
        var size_pad = Int64.MaxValue.ToString ().Length;

        if (!include_mdb) {
            debug_files.Clear ();
        }

        if (blacklist_file != null) {
            using (var reader = new StreamReader (blacklist_file)) {
                string line = null;
                while ((line = reader.ReadLine ()) != null) {
                    line = line.Trim ();
                    if (!String.IsNullOrEmpty (line) && line[0] != '#') {
                        blacklist.Add (line);
                    }
                }
            }
        }

        for (int i = 0; i < items.Count; i++) {
            if (items[i].Count <= 0) {
                continue;
            }
           
            Console.WriteLine (labels[i]);
            Console.WriteLine (String.Empty.PadRight (labels[i].Length, '-'));

            var result = from path in 
                items[i].Except (blacklist, new BlacklistComparer ())
                orderby new FileInfo (path).Length descending
                select path;

            foreach (var path in result) {
                var size_path = path;
                if (items[i] == native_files && strip_binaries) {
                    size_path = StripBinary (path);
                }
                var stat = new FileInfo (size_path);
                sizes[i] += stat.Length;
                total_size += stat.Length;
                Console.WriteLine ("{0}{1}", stat.Length.ToString ().PadRight (size_pad, ' '), path);
            }

            Console.WriteLine ();
        }

        var max_len = (from l in labels orderby l.Length select l.Length).Last () + 15;

        Console.WriteLine ("Summary");
        Console.WriteLine ("-------");

        for (int i = 0; i < items.Count; i++) {
            if (items[i].Count > 0) {
                Console.WriteLine ("{0}: {1} ({2:0.000} MB)", 
                    String.Format ("Total {0} Size", labels[i]).PadLeft (max_len, ' '),
                    sizes[i].ToString ().PadRight (10, ' '), sizes[i] / 1024.0 / 1024.0);
            }
        }

        Console.WriteLine ();
        Console.WriteLine ("{0}  {1} ({2:0.000} MB)",
            String.Empty.PadRight (max_len, ' '),
            total_size.ToString ().PadRight (10, ' '), total_size / 1024.0 / 1024.0);

        try {
            Directory.Delete (strip_dir, true);
        } catch {
        }
    }

    private static void Walk (string path)
    {
        if (Directory.Exists (path)) {
            WalkDir (new DirectoryInfo (path));
        } else {
            LoadFile (new FileInfo (path));
        }
    }

    private static void WalkDir (DirectoryInfo dir)
    {
        if (dir.Name.StartsWith (".") && dir.Name != ".") {
            return;
        }

        foreach (var child_dir in dir.GetDirectories ()) {
            WalkDir (child_dir);
        }

        foreach (var file in dir.GetFiles ()) {
            LoadFile (file);
        }
    }

    private static void LoadFile (FileInfo file)
    {
        if (file.Name.StartsWith (".")) {
            return;
        }

        switch (Path.GetExtension (file.Name)) {
            case ".dll":
            case ".exe":
                LoadAssembly (file);
                break;
            case ".mdb":
                LoadDebug (file);
                break;
            case ".la":
            case ".a":
                break;
            default:
                if (!LddFile (file)) {
                    LoadMisc (file);
                }
                break;
        }
    }

    private static void LoadDebug (FileInfo file)
    {
        if (!debug_files.Contains (file.FullName)) {
            debug_files.Add (file.FullName);
        }
    }

    public static void LoadMisc (FileInfo file)
    {
        if (!misc_files.Contains (file.FullName)) {
            misc_files.Add (file.FullName);
        }
    }

    private static void LoadAssembly (FileInfo file)
    {
        LoadAssembly (Assembly.LoadFrom (file.FullName));
    }

    private static void LoadAssembly (Assembly assembly)
    {
        if (!assemblies.Exists ((a) => a.Location == assembly.Location)) {
            ReadAssembly (assembly);
        }

        foreach (var rname in assembly.GetReferencedAssemblies ()) {
            if (!assemblies.Exists ((a) => a.FullName == rname.FullName)) {
                ReadAssembly (Assembly.Load (rname.FullName));
            }
        }
    }

    private static void ReadAssembly (Assembly assembly)
    {
        BindingFlags binding_flags = 
            BindingFlags.NonPublic | 
            BindingFlags.Public | 
            BindingFlags.Instance | 
            BindingFlags.Static;

        assemblies.Add (assembly);

        var pinvoke_modules = new List<string> ();

        foreach (var type in assembly.GetTypes ()) {
            foreach (var method in type.GetMethods (binding_flags)) {
                if ((method.Attributes & MethodAttributes.PinvokeImpl) != 0) {
                    foreach (var attr in method.GetCustomAttributes (typeof (DllImportAttribute), true)) {
                        string module = ((DllImportAttribute)attr).Value;
                        if (!pinvoke_modules.Contains (module)) {
                            pinvoke_modules.Add (module);
                        }
                    }
                }
            }
        }

        foreach (var module in pinvoke_modules) {
            var lib = LocateNativeLibrary (assembly.Location, module);
            if (lib != null) {
                AddNativeLib (lib);
            }
        }
    }
    
    private static string LocateNativeLibrary (string assemblyPath, string pinvokeModule)
    {
        var asm_config = assemblyPath + ".config"; 
        var module = pinvokeModule;

        if (File.Exists (asm_config)) {
            module = FindNativeLibraryForDllMap (asm_config, pinvokeModule);
        }
        
        if (module == pinvokeModule && File.Exists ("/etc/mono/config")) {
            module = FindNativeLibraryForDllMap ("/etc/mono/config", pinvokeModule);
        }

        pinvokeModule = module;

        foreach (var path in search_paths) {
            var names = new string [] {
                pinvokeModule,
                pinvokeModule + ".so",
                "lib" + pinvokeModule,
                "lib" + pinvokeModule + ".so"
            };

            foreach (var name in names) {
                var full_name = Path.Combine (path, name);
                if (File.Exists (full_name)) {
                    return full_name;
                }
            }
        }

        return null;
    }

    private static string FindNativeLibraryForDllMap (string asmConfig, string pinvokeModule)
    {
        var navigator = new XPathDocument (asmConfig).CreateNavigator ();
        var expression = navigator.Compile ("/configuration/dllmap");
        var iter = navigator.Select (expression);
            
        while (iter.MoveNext ()) {
            if (iter.Current.GetAttribute ("dll", navigator.NamespaceURI) != pinvokeModule) {
                continue;
            }

            var os = iter.Current.GetAttribute ("os", navigator.NamespaceURI);
            if (String.IsNullOrEmpty (os) || os == "linux" || os == "!windows") {
                return iter.Current.GetAttribute ("target", navigator.NamespaceURI);
            }
        }

        return pinvokeModule;
    }

    private static void FindNativeLibrarySearchPaths ()
    {
        foreach (var path in Environment.GetEnvironmentVariable ("LD_LIBRARY_PATH").Split (':')) {
            search_paths.Add (path);
        }
        
        FindNativeLibrarySearchPaths ("/etc/ld.so.conf");
    }

    private static void FindNativeLibrarySearchPaths (string ldConfigPath)
    {
        string line;

        using (var reader = new StreamReader (ldConfigPath)) {
            while ((line = reader.ReadLine ()) != null) {
                line = line.Trim ();
                if (line.StartsWith ("include ")) {
                    var expr = line.Substring (8);
                    var dir = Path.GetDirectoryName (expr);
                    expr = expr.Substring (expr.LastIndexOf (Path.DirectorySeparatorChar) + 1);
                    foreach (var path in Directory.GetFiles (dir, expr)) {
                        FindNativeLibrarySearchPaths (path);
                    }
                } else {
                    search_paths.Add (line);
                }
            }
        }
    }

    private static bool LddFile (string path)
    {
        var file = new FileInfo (path);
        if (file.Exists) {
            return LddFile (file);
        }
        return false;
    }

    private static List<string> ldded_files = new List<string> ();

    private static bool LddFile (FileInfo file)
    { 
        if (ldded_files.Contains (file.FullName)) {
            return native_files.Contains (file.FullName);
        }

        ldded_files.Add (file.FullName);
        
        var proc = Process.Start (new ProcessStartInfo ("ldd", file.FullName) {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        });

        proc.WaitForExit ();
        if (proc.ExitCode != 0) {
            return false;
        }
     
        if (!AddNativeLib (file.FullName)) {
            return true;
        }

        string line;
        while ((line = proc.StandardOutput.ReadLine ()) != null) {
            line = line.Trim ();
            int addr = line.IndexOf ('(');
            if (addr < 0) {
                continue;
            }

            line = line.Substring (0, addr);
            int map = line.IndexOf (" => ");
            if (map > 0) {
                line = line.Substring (map + 4);
            }

            AddNativeLib (line.Trim ());
        }

        return true;
    }

    private static bool AddNativeLib (string path)
    {
        if (String.IsNullOrEmpty (path) || path[0] != '/') {
            return false;
        } else if (!File.Exists (path)) {
            throw new IOException ("File does not exist: " + path);
        } else if (native_files.Contains (path)) {
            return false;
        }

        native_files.Add (path);
        LddFile (path);
        return true;
    }

    private static string StripBinary (string path)
    {
        var out_path = Path.Combine (strip_dir, Path.GetFileName (path));
        Directory.CreateDirectory (strip_dir);
        var proc = Process.Start (new ProcessStartInfo ("strip", 
            String.Format ("-g \"{0}\" -o \"{1}\"", path, out_path)) {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        });
        proc.WaitForExit ();
        return proc.ExitCode == 0 ? out_path : path;
    }
}
