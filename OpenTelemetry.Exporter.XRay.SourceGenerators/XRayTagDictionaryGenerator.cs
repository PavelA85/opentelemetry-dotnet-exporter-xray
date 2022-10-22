﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace OpenTelemetry.Exporter.XRay.SourceGenerators
{
    [Generator]
    internal class XRayTagDictionaryGenerator : ISourceGenerator
    {
        private const int BitsPerMask = 62;

        private class Entry
        {
            public string Name { get; set; }
            public string Value { get; set; }
            public string BitName { get; set; }
            public string BitMask { get; set; }
        }

        public void Execute(GeneratorExecutionContext context)
        {
            var syntaxReceiver = (XRayTagDictionarySyntaxReceiver)context.SyntaxReceiver;
            var userClass = syntaxReceiver?.XRayConventionsClass;
            if (userClass == null)
            {
                return;
            }

            var semanticModel = context.Compilation.GetSemanticModel(userClass.SyntaxTree);

            var body = new StringBuilder();
            var list = new List<Entry>();
            foreach (var item in userClass.Members.OfType<FieldDeclarationSyntax>())
            {
                foreach (var variable in item.Declaration.Variables)
                {
                    var symbol = (IFieldSymbol)semanticModel.GetDeclaredSymbol(variable);
                    if (symbol == null || !symbol.IsConst)
                        continue;

                    if (symbol.ConstantValue is string value)
                        list.Add(new Entry { Name = symbol.Name, Value = value });
                }
            }

            list.Sort((x, y) =>
            {
                if (x.Value.Length != y.Value.Length)
                    return x.Value.Length - y.Value.Length;

                return StringComparer.Ordinal.Compare(x.Value, y.Value);
            });

            body.AppendLine("        public struct TagValues");
            body.AppendLine("        {");

            var flagCount = (list.Count + BitsPerMask - 1) / BitsPerMask;
            for (var i = 0; i < list.Count; i++)
            {
                var bitName = $"{i / BitsPerMask}";
                var bitMask = $"0x{1L << (i % BitsPerMask):X}L";
                list[i].BitName = bitName;
                list[i].BitMask = bitMask;

                body.AppendLine($"            public object {list[i].Name}; // {bitName}:{bitMask}");
            }

            body.AppendLine("        }");

            body.AppendLine();
            for (var i = 0; i < flagCount; i++)
            {
                body.AppendLine($"        private long _bits{i};");
                body.AppendLine($"        private long _consumed{i};");
            }

            body.AppendLine("        private TagValues _values;");
            body.AppendLine();

            body.AppendLine("        public bool IsEmpty");
            body.AppendLine("        {");
            body.AppendLine("            get");
            body.AppendLine("            {");
            body.AppendLine("                return _bits0 == 0");
            for (var i = 1; i < flagCount; i++)
                body.AppendLine($"                    && _bits{i} == 0");
            body.AppendLine($"                    && (_extraCount == 0)");
            body.AppendLine("                ;");
            body.AppendLine("            }");
            body.AppendLine("        }");
            body.AppendLine();

            foreach (var item in list)
            {
                body.AppendLine($"        public bool TryGet{item.Name}(out object value)");
                body.AppendLine($"        {{");
                body.AppendLine($"            if ((_bits{item.BitName} & {item.BitMask}) != 0)");
                body.AppendLine($"            {{");
                body.AppendLine($"                _consumed{item.BitName} |= {item.BitMask};");
                body.AppendLine($"                value = _values.{item.Name};");
                body.AppendLine($"                return true;");
                body.AppendLine($"            }}");
                body.AppendLine();
                body.AppendLine($"            value = null;");
                body.AppendLine($"            return false;");
                body.AppendLine($"        }}");
                body.AppendLine();
            }

            EmitClear(body, flagCount, list);
            EmitConsume(body, flagCount);
            EmitResetConsume(body, flagCount);
            EmitAddOrReplace(body, list);

            EmitEnumerator(body, flagCount, list);

            EmitState(body, flagCount);

            // Build up the source code
            var source = $@"// <auto-generated/>
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;

namespace OpenTelemetry.Exporter.XRay.Implementation
{{
    internal partial class XRayTagDictionary
    {{
{body}
    }}
}}
";
            // Add the source code to the compilation
            context.AddSource("XRayTagDictionary.g.cs", source);
        }

        private static void EmitState(StringBuilder body, int flagCount)
        {
            body.AppendLine("        public partial struct State");
            body.AppendLine("        {");
            body.AppendLine($"            private XRayTagDictionary _dictionary;");

            for (var i = 0; i < flagCount; i++)
                body.AppendLine($"            private long _bits{i};");

            body.AppendLine();
            body.AppendLine($"            public State(XRayTagDictionary dictionary)");
            body.AppendLine($"            {{");
            body.AppendLine($"                _dictionary = dictionary;");
            for (var i = 0; i < flagCount; i++)
                body.AppendLine($"                _bits{i} = dictionary._bits{i};");
            body.AppendLine($"            }}");

            body.AppendLine();
            body.AppendLine($"            public void Restore()");
            body.AppendLine($"            {{");
            for (var i = 0; i < flagCount; i++)
                body.AppendLine($"                _dictionary._bits{i} = _bits{i};");
            body.AppendLine($"            }}");

            body.AppendLine("        }");
        }

        private static void EmitEnumerator(StringBuilder body, int flagCount, List<Entry> list)
        {
            body.AppendLine("        public partial struct Enumerator");
            body.AppendLine("        {");

            EmitGetNext(body, flagCount);
            EmitMoveNext(body, list);

            body.AppendLine("        }");
            body.AppendLine();
        }

        private static void EmitMoveNext(StringBuilder body, List<Entry> list)
        {
            body.AppendLine("            public bool MoveNext()");
            body.AppendLine("            {");

            body.AppendLine("                switch (_next)");
            body.AppendLine("                {");

            for (var i = 0; i < list.Count; i++)
            {
                var item = list[i];
                body.AppendLine($"                    case {i}:");
                body.AppendLine($"                        Debug.Assert((_currentBits & {item.BitMask}) != 0);");
                body.AppendLine($"                        _current = new KeyValuePair<string, object>(XRayConventions.{item.Name}, _dictionary._values.{item.Name});");
                body.AppendLine($"                        _currentBits ^= {item.BitMask};");
                body.AppendLine($"                        break;");
            }

            body.AppendLine($"                    default:");
            body.AppendLine($"                        if (_extraIndex >= _dictionary._extraCount)");
            body.AppendLine($"                        {{");
            body.AppendLine($"                            _current = default(KeyValuePair<string, object>);");
            body.AppendLine($"                            return false;");
            body.AppendLine($"                        }}");
            body.AppendLine();
            body.AppendLine($"                        _current = _dictionary._extra[_extraIndex];");
            body.AppendLine($"                        _extraIndex++;");
            body.AppendLine($"                        return true;");

            body.AppendLine("                }");


            body.AppendLine();
            body.AppendLine("                GetNext();");
            body.AppendLine("                return true;");
            body.AppendLine("            }");
        }

        private static void EmitGetNext(StringBuilder body, int flagCount)
        {
            body.AppendLine("            private void GetNext()");
            body.AppendLine("            {");
            body.AppendLine("                var bitCount = BitOperations.TrailingZeroCount(_currentBits);");
            body.AppendLine();

            body.AppendLine("                switch (_currentIndex)");
            body.AppendLine("                {");
            for (var i = 0; i < flagCount - 1; i++)
            {
                body.AppendLine($"                    case {i}:");
                body.AppendLine($"                        if (_currentBits != 0)");
                body.AppendLine($"                        {{");
                body.AppendLine($"                            _next = bitCount + {BitsPerMask * i};");
                body.AppendLine($"                            return;");
                body.AppendLine($"                        }}");
                body.AppendLine();
                body.AppendLine($"                        _currentBits = _dictionary._bits{i + 1};");
                body.AppendLine($"                        _currentIndex = {i + 1};");
                body.AppendLine($"                        bitCount = BitOperations.TrailingZeroCount(_currentBits);;");
                body.AppendLine($"                        goto case {i + 1};");
            }

            body.AppendLine($"                    case {flagCount - 1}:");
            body.AppendLine($"                        if (_currentBits != 0)");
            body.AppendLine($"                        {{");
            body.AppendLine($"                            _next = bitCount + {BitsPerMask * (flagCount - 1)};");
            body.AppendLine($"                            return;");
            body.AppendLine($"                        }}");
            body.AppendLine();
            body.AppendLine($"                        _currentBits = 0;");
            body.AppendLine($"                        _currentIndex = {flagCount};");
            body.AppendLine($"                        _next = -1;");
            body.AppendLine($"                        break;");

            body.AppendLine("                }");
            body.AppendLine("            }");
        }

        private static void EmitAddOrReplace(StringBuilder body, List<Entry> list)
        {
            body.AppendLine("        private void AddOrReplace(in KeyValuePair<string, object> pair, bool ignoreExtra)");
            body.AppendLine("        {");
            body.AppendLine("            var (key, value) = pair;");
            body.AppendLine();
            
            body.AppendLine("            switch (key.Length)");
            body.AppendLine("            {");

            var byLength = list.GroupBy(s => s.Value.Length).OrderBy(s => s.Key);
            foreach (var item in byLength)
            {
                body.AppendLine($"                case {item.Key}:");
                body.AppendLine("                {");

                foreach (var tag in item)
                {
                    body.AppendLine($"                    if (ReferenceEquals(key, XRayConventions.{tag.Name}))");
                    body.AppendLine($"                    {{");
                    body.AppendLine($"                        _values.{tag.Name} = value;");
                    body.AppendLine($"                        _bits{tag.BitName} |= {tag.BitMask};");
                    body.AppendLine($"                        return;");
                    body.AppendLine($"                    }}");
                }

                body.AppendLine();
                foreach (var tag in item)
                {
                    body.AppendLine($"                    if (XRayConventions.{tag.Name}.Equals(key, StringComparison.OrdinalIgnoreCase))");
                    body.AppendLine($"                    {{");
                    body.AppendLine($"                        _bits{tag.BitName} |= {tag.BitMask};");
                    body.AppendLine($"                        _values.{tag.Name} = value;");
                    body.AppendLine($"                        return;");
                    body.AppendLine($"                    }}");
                }

                body.AppendLine("                    break;");
                body.AppendLine("                }");
            }

            body.AppendLine("            }");

            body.AppendLine();
            body.AppendLine("            if (!ignoreExtra)");
            body.AppendLine("                Append(pair);");
            body.AppendLine("        }");
        }

        private static void EmitClear(StringBuilder body, int flagCount, List<Entry> list)
        {
            body.AppendLine("        public void Clear()");
            body.AppendLine("        {");
            for (var i = 0; i < flagCount; i++)
            {
                body.AppendLine($"            _bits{i} = 0;");
                body.AppendLine($"            _consumed{i} = 0;");
            }

            body.AppendLine();

            body.AppendLine($"            _values = default(TagValues);");

            body.AppendLine("            ResetBuffer();");
            body.AppendLine("        }");
            body.AppendLine();
        }

        private static void EmitConsume(StringBuilder body, int flagCount)
        {
            body.AppendLine("        public void Consume()");
            body.AppendLine("        {");
            for (var i = 0; i < flagCount; i++)
            {
                body.AppendLine($"            _bits{i} &= ~_consumed{i};");
                body.AppendLine($"            _consumed{i} = 0;");
            }

            body.AppendLine("        }");
            body.AppendLine();
        }

        private static void EmitResetConsume(StringBuilder body, int flagCount)
        {
            body.AppendLine("        public void ResetConsume()");
            body.AppendLine("        {");
            for (var i = 0; i < flagCount; i++)
            {
                body.AppendLine($"            _consumed{i} = 0;");
            }

            body.AppendLine("        }");
            body.AppendLine();
        }

        public void Initialize(GeneratorInitializationContext context)
        {
            context.RegisterForSyntaxNotifications(() => new XRayTagDictionarySyntaxReceiver());
        }
    }

    public class XRayTagDictionarySyntaxReceiver : ISyntaxReceiver
    {
        public ClassDeclarationSyntax XRayConventionsClass { get; private set; }

        public void OnVisitSyntaxNode(SyntaxNode syntaxNode)
        {
            if (syntaxNode is ClassDeclarationSyntax cds && cds.Identifier.ValueText == "XRayConventions")
                XRayConventionsClass = cds;
        }
    }
}