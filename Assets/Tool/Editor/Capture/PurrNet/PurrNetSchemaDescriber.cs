using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using PurrNet.Packing;
using PurrNet.Utils;

namespace PurrNet.Services.Capture
{
    /// <summary>
    /// Describes, for the build manifest, how a networked type's RPCs and modules appear on the wire: which RPC id
    /// is which method with which parameters, and which module index is which field. Read from the compiled,
    /// post-processed assemblies, so the ids are the ones the build uses.
    /// </summary>
    public sealed class PurrNetSchemaDescriber
    {
        private const BindingFlags Declared =
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        private readonly JObject _types = new JObject();
        private readonly JObject _layouts = new JObject();
        private readonly JObject _hashes = new JObject();

        /// <summary>Values whose encoding the decoder implements itself; they need no layout.</summary>
        private static readonly HashSet<Type> BuiltIn = new HashSet<Type>
        {
            typeof(bool), typeof(byte), typeof(sbyte), typeof(short), typeof(ushort), typeof(int), typeof(uint),
            typeof(long), typeof(ulong), typeof(float), typeof(double), typeof(char), typeof(string),
            typeof(UnityEngine.Vector2), typeof(UnityEngine.Vector3), typeof(UnityEngine.Vector4),
            typeof(UnityEngine.Quaternion), typeof(UnityEngine.Vector2Int), typeof(UnityEngine.Vector3Int),
            typeof(PackedULong), typeof(PackedUInt), typeof(PackedUShort), typeof(PackedByte), typeof(PackedInt),
            typeof(PackedLong), typeof(Size), typeof(CompressedFloat), typeof(NormalizedFloat), typeof(PlayerID)
        };
        private readonly Dictionary<Type, List<RPCManifestEntry>> _rpcsByType;

        public JObject types => _types;

        /// <summary>How the values that cross the wire are laid out: generated structs field by field, enums by width.</summary>
        public JObject layouts => _layouts;

        /// <summary>
        /// PurrNet's type hash of every type described, and of the built-in value types: generic RPCs name their type
        /// arguments by it, static RPCs their class.
        /// </summary>
        public JObject hashes => _hashes;

        public PurrNetSchemaDescriber(IEnumerable<RPCManifestEntry> rpcs)
        {
            _rpcsByType = rpcs.Where(e => e.declaringType != null)
                .GroupBy(e => e.declaringType)
                .ToDictionary(g => g.Key, g => g.ToList());

            foreach (var type in BuiltIn)
                Remember(type);
        }

        public static string NameOf(Type type)
        {
            if (type.IsArray)
                return NameOf(type.GetElementType()) + "[]";

            if (!type.IsGenericType)
                return type.FullName ?? type.Name;

            var definition = type.GetGenericTypeDefinition().FullName ?? type.Name;
            int tick = definition.IndexOf('`');
            if (tick >= 0)
                definition = definition.Substring(0, tick);

            var builder = new StringBuilder(definition).Append('<');
            var arguments = type.GetGenericArguments();
            for (int i = 0; i < arguments.Length; i++)
            {
                if (i > 0)
                    builder.Append(',');
                builder.Append(NameOf(arguments[i]));
            }

            return builder.Append('>').ToString();
        }

        private void Remember(Type type)
        {
            if (type == null || type.IsGenericParameter || type.ContainsGenericParameters)
                return;
            _hashes[Hasher.Hash(type).ToString()] = NameOf(type);
        }

        public string Describe(Type type)
        {
            Remember(type);
            string name = NameOf(type);
            if (_types.ContainsKey(name))
                return name;

            var entry = new JObject();
            _types[name] = entry;

            entry["rpcs"] = DescribeRpcs(type);

            if (typeof(NetworkIdentity).IsAssignableFrom(type))
            {
                var modules = new JArray();
                int next = 0;
                AddModules(type, modules, ref next);
                entry["modules"] = modules;
            }

            return name;
        }

        private JArray DescribeRpcs(Type type)
        {
            var rpcs = new JArray();
            for (var current = type; current != null && current != typeof(object); current = current.BaseType)
            {
                var key = current.IsGenericType ? current.GetGenericTypeDefinition() : current;
                if (!_rpcsByType.TryGetValue(key, out var entries))
                    continue;

                foreach (var rpc in entries.OrderBy(e => e.rpcId))
                    rpcs.Add(DescribeRpc(current, rpc));
            }

            return new JArray(rpcs.OrderBy(r => (int)r["id"]));
        }

        private JObject DescribeRpc(Type declaring, RPCManifestEntry rpc)
        {
            var signature = rpc.signature;
            var described = new JObject
            {
                ["id"] = rpc.rpcId,
                ["name"] = signature.rpcName,
                ["kind"] = signature.type.ToString(),
                ["channel"] = signature.channel.ToString(),
                ["static"] = signature.isStatic,
                ["compression"] = signature.compressionLevel.ToString(),
                ["delta"] = signature.deltaPacked && signature.type != RPCType.ServerRPC,
                ["immediate"] = signature.immediate
            };

            var method = declaring.GetMethods(Declared).FirstOrDefault(m => m.Name == $"{signature.rpcName}_Original_{rpc.rpcId}");
            if (method == null)
            {
                described["params"] = null;
                return described;
            }

            var parameters = method.GetParameters().ToList();
            if (signature.type == RPCType.TargetRPC && parameters.Count > 0)
                parameters.RemoveAt(0);
            if (parameters.Count > 0 && parameters[parameters.Count - 1].ParameterType == typeof(RPCInfo))
                parameters.RemoveAt(parameters.Count - 1);

            var list = new JArray();
            foreach (var parameter in parameters)
            {
                DescribeValue(parameter.ParameterType);
                list.Add(new JObject { ["name"] = parameter.Name, ["type"] = NameOf(parameter.ParameterType) });
            }

            described["params"] = list;
            described["awaitable"] = IsAwaitable(method.ReturnType);
            described["genericArity"] = method.IsGenericMethodDefinition ? method.GetGenericArguments().Length : 0;
            described["returns"] = NameOf(method.ReturnType);
            return described;
        }

        private static bool IsAwaitable(Type type)
        {
            if (type == typeof(Task) || type == typeof(IEnumerator))
                return true;
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
                return true;
            return type.FullName != null && type.FullName.StartsWith("Cysharp.Threading.Tasks.UniTask", StringComparison.Ordinal);
        }

        /// <summary>
        /// Module indices, as the generated init assigns them: fields in declaration order, base class first, and
        /// each module's own modules right after it.
        /// </summary>
        private void AddModules(Type type, JArray modules, ref int next)
        {
            var chain = new List<Type>();
            for (var current = type; current != null && current != typeof(object); current = current.BaseType)
                chain.Add(current);
            chain.Reverse();

            foreach (var current in chain)
            {
                foreach (var field in current.GetFields(Declared).Where(f => !f.IsStatic).OrderBy(f => f.MetadataToken))
                {
                    if (!typeof(NetworkModule).IsAssignableFrom(field.FieldType))
                        continue;

                    string moduleType = Describe(field.FieldType);
                    modules.Add(new JObject
                    {
                        ["index"] = next++,
                        ["field"] = field.Name,
                        ["type"] = moduleType
                    });

                    AddModules(field.FieldType, modules, ref next);
                }
            }
        }

        public static uint HashOf(Type type) => Hasher.Hash(type);

        /// <summary>Adds the layout of a value type, and of every type inside it, unless the decoder knows it already.</summary>
        public void DescribeValue(Type type)
        {
            Remember(type);
            if (type == null || type.IsGenericParameter || BuiltIn.Contains(type))
                return;

            string name = NameOf(type);
            if (_layouts.ContainsKey(name))
                return;

            if (type.IsArray)
            {
                _layouts[name] = new JObject { ["kind"] = "array", ["element"] = NameOf(type.GetElementType()) };
                DescribeValue(type.GetElementType());
                return;
            }

            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Nullable<>))
            {
                var inner = type.GetGenericArguments()[0];
                _layouts[name] = new JObject { ["kind"] = "nullable", ["element"] = NameOf(inner) };
                DescribeValue(inner);
                return;
            }

            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>))
            {
                var arguments = type.GetGenericArguments();
                _layouts[name] = new JObject { ["kind"] = "map", ["key"] = NameOf(arguments[0]), ["element"] = NameOf(arguments[1]), ["sealed"] = type.IsSealed };
                DescribeValue(arguments[0]);
                DescribeValue(arguments[1]);
                return;
            }

            if (type.IsGenericType && (type.GetGenericTypeDefinition() == typeof(List<>) ||
                                       type.GetGenericTypeDefinition() == typeof(HashSet<>) ||
                                       type.GetGenericTypeDefinition() == typeof(Queue<>)))
            {
                var inner = type.GetGenericArguments()[0];
                _layouts[name] = new JObject { ["kind"] = "list", ["element"] = NameOf(inner), ["sealed"] = type.IsSealed };
                DescribeValue(inner);
                return;
            }

            if (type.IsEnum)
            {
                var values = new JObject();
                foreach (var value in Enum.GetValues(type))
                    values[Convert.ToInt64(value).ToString()] = value.ToString();
                _layouts[name] = new JObject
                {
                    ["kind"] = "enum",
                    ["underlying"] = NameOf(Enum.GetUnderlyingType(type)),
                    ["values"] = values
                };
                return;
            }

            string packer = PackerKind(type);
            var layout = new JObject
            {
                ["kind"] = type.IsValueType ? "struct" : "class",
                ["packer"] = packer,
                ["sealed"] = type.IsSealed,
                ["hash"] = Hasher.Hash(type)
            };
            _layouts[name] = layout;

            if (packer != "generated")
                return;

            var fields = new JArray();
            var chain = new List<Type>();
            for (var current = type; current != null && current != typeof(object) && current != typeof(ValueType); current = current.BaseType)
                chain.Add(current);
            chain.Reverse();

            foreach (var current in chain)
            {
                foreach (var field in current.GetFields(Declared).Where(f => !f.IsStatic).OrderBy(f => f.MetadataToken))
                {
                    if (typeof(Delegate).IsAssignableFrom(field.FieldType))
                        continue;
                    if (field.IsDefined(typeof(DontPackAttribute), false) || field.FieldType.IsDefined(typeof(DontPackAttribute), false))
                        continue;

                    DescribeValue(field.FieldType);
                    fields.Add(new JObject { ["name"] = CleanName(field.Name), ["type"] = NameOf(field.FieldType) });
                }
            }

            layout["fields"] = fields;
        }

        /// <summary>
        /// "generated" when PurrNet's code generator wrote the serializer from the fields, "custom" when the type
        /// writes itself (IPacked, IPackedSimple) or someone registered a hand-written packer, "fallback" when the
        /// value goes through PurrNet's polymorphic writer.
        /// </summary>
        private static string PackerKind(Type type)
        {
            if (typeof(IPacked).IsAssignableFrom(type) || typeof(IPackedSimple).IsAssignableFrom(type))
                return "custom";

            try
            {
                var field = typeof(Packer<>).MakeGenericType(type).GetField("WriteFunc", BindingFlags.Public | BindingFlags.Static);
                if (field?.GetValue(null) is Delegate writer)
                {
                    var declaring = writer.Method.DeclaringType;
                    if (declaring == typeof(Packer))
                        return "fallback";
                    if (declaring != null && declaring.IsDefined(typeof(GeneratedByILAttribute), false))
                        return "generated";
                    return "custom";
                }
            }
            catch (ArgumentException)
            {
            }

            return "unknown";
        }

        private static string CleanName(string name)
        {
            if (name.Length > 0 && name[0] == '<')
            {
                int end = name.IndexOf('>');
                if (end > 1)
                    return name.Substring(1, end - 1);
            }
            return name;
        }

    }
}
