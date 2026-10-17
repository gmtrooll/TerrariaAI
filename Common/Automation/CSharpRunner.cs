using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using FirstMod_Sp.Common.Systems;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Terraria;
using Terraria.ModLoader;
using Terraria.ModLoader.Core;

namespace FirstMod_Sp.Common.Automation
{
    /// <summary>
    /// Base class of every snippet. Snippet code is the body of Run(), so it can call these members directly:
    /// <c>Print(Main.time); return Bots.Count;</c>
    /// </summary>
    public abstract class SnippetBase
    {
        public readonly List<string> Output = new List<string>();

        public void Print(object value) {
            Output.Add(value?.ToString() ?? "null");
        }

        /// <summary>The local player, or null on a server.</summary>
        public static Player Me => Main.dedServ ? null : Main.LocalPlayer;

        public static List<Player> Bots => BotManagerSystem.ActiveBots.Select(i => Main.player[i]).ToList();

        public static Player Bot(int n) => Main.player[BotManagerSystem.ActiveBots[n]];

        public abstract object Run();
    }

    /// <summary>
    /// Compiles C# snippets at runtime with the Roslyn compiler that ships with tModLoader, against the loaded game
    /// and this mod, and loads them into this mod's load context so they see the same types as the running code.
    /// </summary>
    public static class CSharpRunner
    {
        public const string AssemblyPrefix = "snippet_";

        private const string Usings =
            "using System; using System.Collections.Generic; using System.Linq; using System.Text; " +
            "using Microsoft.Xna.Framework; using Terraria; using Terraria.ID; using Terraria.ModLoader; " +
            "using Terraria.DataStructures; using Terraria.GameContent; " +
            "using FirstMod_Sp; using FirstMod_Sp.Common.Systems; using FirstMod_Sp.Common.Automation; " +
            "using FirstMod_Sp.Common.Bots; using FirstMod_Sp.Common.Benchmark;\n";

        private static List<MetadataReference> references;
        private static int counter;

        public sealed class Compiled
        {
            public Type SnippetType;
            public double CompileMs;

            public SnippetBase NewInstance() => (SnippetBase)Activator.CreateInstance(SnippetType);
        }

        /// <summary>
        /// Code is a method body. A single expression without a trailing ';' is returned as the result;
        /// a body without a return statement returns null.
        /// </summary>
        public static Compiled CompileBody(string code, out string errors) {
            string trimmed = code.Trim();
            bool isExpression = !trimmed.EndsWith(";") && !trimmed.EndsWith("}");
            string body = isExpression ? "return (object)(" + trimmed + ");" : code + "\nreturn null;";
            return Compile(body, out errors);
        }

        /// <summary>A boolean expression, evaluated by wait_until after every tick.</summary>
        public static Compiled CompileCondition(string expression, out string errors) {
            return Compile("return (object)(bool)(" + expression + ");", out errors);
        }

        private static Compiled Compile(string body, out string errors) {
            var watch = Stopwatch.StartNew();
            references ??= BuildReferences();

            string className = AssemblyPrefix + (++counter);
            string source = Usings +
                "public sealed class " + className + " : SnippetBase {\n" +
                "public override object Run() {\n" +
                "#line 1\n" +
                body + "\n" +
                "#line default\n" +
                "}\n}\n";

            var compilation = CSharpCompilation.Create(
                className,
                new[] { CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest)) },
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                    optimizationLevel: OptimizationLevel.Release, allowUnsafe: true));

            using var image = new MemoryStream();
            var result = compilation.Emit(image);
            if (!result.Success) {
                errors = string.Join("\n", result.Diagnostics
                    .Where(d => d.Severity == DiagnosticSeverity.Error)
                    .Select(d => d.ToString()));
                return null;
            }

            image.Position = 0;
            Assembly assembly = AssemblyLoadContext.GetLoadContext(typeof(CSharpRunner).Assembly).LoadFromStream(image);
            errors = null;
            return new Compiled { SnippetType = assembly.GetType(className), CompileMs = watch.Elapsed.TotalMilliseconds };
        }

        private static List<MetadataReference> BuildReferences() {
            var list = new List<MetadataReference>();
            var seen = new HashSet<string>();
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies()) {
                if (assembly.IsDynamic || string.IsNullOrEmpty(assembly.Location)) continue;
                if (seen.Add(assembly.GetName().Name)) list.Add(MetadataReference.CreateFromFile(assembly.Location));
            }
            list.Add(MetadataReference.CreateFromImage(GetOwnAssemblyImage()));
            return list;
        }

        /// <summary>This mod is loaded from memory (no file location), so read its dll out of the .tmod.</summary>
        private static byte[] GetOwnAssemblyImage() {
            Mod mod = ModContent.GetInstance<global::FirstMod_Sp.FirstMod_Sp>();
            PropertyInfo fileProperty = typeof(Mod).GetProperty("File", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            var file = (TmodFile)fileProperty.GetValue(mod);
            string dllName = file.Name + ".dll";
            if (file.IsOpen) return file.GetBytes(dllName);
            using (file.Open()) {
                return file.GetBytes(dllName);
            }
        }

        /// <summary>Drop cached references, e.g. after mods were reloaded.</summary>
        public static void Reset() {
            references = null;
        }
    }
}
