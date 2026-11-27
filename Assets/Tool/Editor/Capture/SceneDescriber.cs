using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using PurrNet.Services.Capture;
using UnityEditor;
using UnityEngine;

namespace PurrNet.Services.Editor.Capture
{
    /// <summary>
    /// Describes the objects of a scene or a prefab that matter to someone reading a match's traffic afterwards:
    /// network identities and colliders, as JSON. Nothing about how an object looks is written (no renderers,
    /// meshes drawn, materials or textures), and of a component's inspector values only PurrNet's own components
    /// keep theirs: a game's scripts are named, nothing more. Objects that carry none of these and have no such
    /// object below them are left out. Collision meshes and terrain heights are not part of the JSON: they are
    /// kept once each, named by the hash of their contents, so a mesh a hundred objects share, or one that did
    /// not change since the last build, is stored a single time.
    /// </summary>
    internal sealed class SceneDescriber
    {
        /// <summary>A build that describes more objects than this stops describing: the rest is left out and the manifest says so.</summary>
        public const int MaxNodes = 250000;

        /// <summary>The most meshes, terrains and assemblies one build keeps: what it asks the website for must stay small.</summary>
        public const int MaxBlobs = 4000;

        const int MaxComponentJson = 16 * 1024;
        const int MaxMeshIndices = 6000000;

        /// <summary>The blobs gathered so far, by hash: what they are and their bytes.</summary>
        public readonly Dictionary<string, (string kind, byte[] data)> blobs =
            new Dictionary<string, (string kind, byte[] data)>();

        readonly Dictionary<UnityEngine.Object, string> _meshHashes = new Dictionary<UnityEngine.Object, string>();
        readonly List<NetworkIdentity> _identities = new List<NetworkIdentity>();
        readonly List<Component> _components = new List<Component>();

        public int nodes { get; private set; }
        public bool truncated { get; private set; }

        /// <summary>Starts counting objects again: the limit is per description.</summary>
        public void ResetCount()
        {
            nodes = 0;
            truncated = false;
        }

        /// <summary>
        /// Lets go of everything that points at loaded assets, so they can be unloaded, and forgets which meshes were
        /// seen: after an unload their ids may name other meshes.
        /// </summary>
        public void Forget()
        {
            _meshHashes.Clear();
            _identities.Clear();
            _components.Clear();
        }

        /// <summary>
        /// Writes the tree under <paramref name="root"/>. <paramref name="order"/> gives each network identity its
        /// place in the list the game itself goes by (a prefab's identities, or a scene's). A root with nothing
        /// worth describing is left out, unless <paramref name="always"/> asks for its name at least.
        /// </summary>
        public void Describe(PartJson json, GameObject root, Dictionary<NetworkIdentity, int> order, bool always)
        {
            if (Interesting(root.transform))
                Node(json, root.transform, order);
            else if (always)
                json.BeginObject().Prop("n", root.name).EndObject();
        }

        /// <summary>The identities under a root in the order PurrNet numbers them.</summary>
        public Dictionary<NetworkIdentity, int> OrderOf(GameObject root)
        {
            var order = new Dictionary<NetworkIdentity, int>();
            root.GetComponentsInChildren(true, _identities);
            for (int i = 0; i < _identities.Count; i++)
                order[_identities[i]] = i;
            return order;
        }

        bool Interesting(Transform transform)
        {
            if (transform.GetComponent<NetworkIdentity>())
                return true;
#if UNITY_PHYSICS_3D
            if (transform.GetComponent<Collider>())
                return true;
#endif
#if UNITY_PHYSICS_2D
            if (transform.GetComponent<Collider2D>())
                return true;
#endif
            for (int i = 0; i < transform.childCount; i++)
            {
                if (Interesting(transform.GetChild(i)))
                    return true;
            }
            return false;
        }

        void Node(PartJson json, Transform transform, Dictionary<NetworkIdentity, int> order)
        {
            if (nodes >= MaxNodes)
            {
                truncated = true;
                json.BeginObject().Prop("n", transform.name).Prop("truncated", true).EndObject();
                return;
            }
            nodes++;

            var go = transform.gameObject;
            json.BeginObject().Prop("n", go.name);
            if (!go.activeSelf)
                json.Prop("off", true);
            if (go.layer != 0)
                json.Prop("layer", go.layer);
            if (!go.CompareTag("Untagged"))
                json.Prop("tag", go.tag);
            if (go.isStatic)
                json.Prop("static", true);

            var p = transform.localPosition;
            var r = transform.localRotation;
            var s = transform.localScale;
            json.Key("t").BeginArray()
                .Value(Round(p.x)).Value(Round(p.y)).Value(Round(p.z))
                .Value(Round(r.x, 100000)).Value(Round(r.y, 100000)).Value(Round(r.z, 100000)).Value(Round(r.w, 100000))
                .Value(Round(s.x)).Value(Round(s.y)).Value(Round(s.z))
                .EndArray();

            go.GetComponents(_components);
            bool any = false;
            for (int i = 0; i < _components.Count; i++)
            {
                var component = _components[i];
                if (!component || !Wanted(component))
                    continue;
                if (!any)
                {
                    any = true;
                    json.Key("c").BeginArray();
                }
                try
                {
                    Describe(json, component, order);
                }
                catch (Exception)
                {
                    json.BeginObject().Prop("k", "failed").Prop("type", component.GetType().FullName).EndObject();
                }
            }
            if (any)
                json.EndArray();

            bool children = false;
            for (int i = 0; i < transform.childCount; i++)
            {
                var child = transform.GetChild(i);
                if (!Interesting(child))
                    continue;
                if (!children)
                {
                    children = true;
                    json.Key("children").BeginArray();
                }
                Node(json, child, order);
            }
            if (children)
                json.EndArray();
            json.EndObject();
        }

        static bool Wanted(Component component)
        {
            if (component is NetworkIdentity)
                return true;
#if UNITY_PHYSICS_3D
            if (component is Collider)
                return true;
#endif
#if UNITY_PHYSICS_2D
            if (component is Collider2D)
                return true;
#endif
            return false;
        }

        static double Round(float value, double scale = 10000) => Math.Round(value * scale) / scale;

        static PartJson Vector(PartJson json, string name, Vector3 v) =>
            json.Key(name).BeginArray().Value(Round(v.x)).Value(Round(v.y)).Value(Round(v.z)).EndArray();

        static PartJson Vector(PartJson json, string name, Vector2 v) =>
            json.Key(name).BeginArray().Value(Round(v.x)).Value(Round(v.y)).EndArray();

        void Describe(PartJson json, Component component, Dictionary<NetworkIdentity, int> order)
        {
            json.BeginObject();
            switch (component)
            {
                case NetworkIdentity identity:
                {
                    var type = identity.GetType();
                    json.Prop("k", "net").Prop("type", type.FullName + ", " + type.Assembly.GetName().Name);
                    if (order != null && order.TryGetValue(identity, out int index))
                        json.Prop("index", index);
                    if (!identity.enabled)
                        json.Prop("disabled", true);
                    // Settings that change what PurrNet sends (a NetworkTransform's, say) are needed to read its
                    // traffic. What a game put in its own scripts is not, and stays in the project.
                    if (type.Assembly.GetName().Name.StartsWith("PurrNet", StringComparison.Ordinal))
                    {
                        string fields = EditorJsonUtility.ToJson(identity);
                        if (fields != null && fields.Length <= MaxComponentJson)
                            json.Raw("fields", fields);
                    }
                    break;
                }
#if UNITY_PHYSICS_3D
                case BoxCollider box:
                    Shape(json, "box", box);
                    Vector(json, "center", box.center);
                    Vector(json, "size", box.size);
                    break;
                case SphereCollider sphere:
                    Shape(json, "sphere", sphere);
                    Vector(json, "center", sphere.center);
                    json.Prop("radius", Round(sphere.radius));
                    break;
                case CapsuleCollider capsule:
                    Shape(json, "capsule", capsule);
                    Vector(json, "center", capsule.center);
                    json.Prop("radius", Round(capsule.radius)).Prop("height", Round(capsule.height))
                        .Prop("direction", capsule.direction);
                    break;
                case CharacterController controller:
                    Shape(json, "character", controller);
                    Vector(json, "center", controller.center);
                    json.Prop("radius", Round(controller.radius)).Prop("height", Round(controller.height));
                    break;
                case MeshCollider mesh:
                    Shape(json, "mesh", mesh);
                    json.Prop("convex", mesh.convex);
                    if (mesh.sharedMesh)
                    {
                        json.Prop("name", mesh.sharedMesh.name);
                        string hash = MeshBlob(mesh.sharedMesh);
                        if (hash != null)
                            json.Prop("blob", hash);
                        else
                        {
                            var bounds = mesh.sharedMesh.bounds;
                            Vector(json, "center", bounds.center);
                            Vector(json, "extents", bounds.extents);
                        }
                    }
                    break;
#if PURRNET_TERRAIN
                case TerrainCollider terrain:
                    Shape(json, "terrain", terrain);
                    if (terrain.terrainData)
                    {
                        var data = terrain.terrainData;
                        Vector(json, "size", data.size);
                        json.Prop("resolution", data.heightmapResolution);
                        string hash = TerrainBlob(data);
                        if (hash != null)
                            json.Prop("blob", hash);
                    }
                    break;
#endif
                case Collider other:
                    Shape(json, "collider", other);
                    json.Prop("type", other.GetType().Name);
                    Vector(json, "center", other.bounds.center);
                    Vector(json, "extents", other.bounds.extents);
                    break;
#endif
#if UNITY_PHYSICS_2D
                case BoxCollider2D box2D:
                    Shape2D(json, "box2d", box2D);
                    Vector(json, "size", box2D.size);
                    break;
                case CircleCollider2D circle:
                    Shape2D(json, "circle2d", circle);
                    json.Prop("radius", Round(circle.radius));
                    break;
                case CapsuleCollider2D capsule2D:
                    Shape2D(json, "capsule2d", capsule2D);
                    Vector(json, "size", capsule2D.size);
                    json.Prop("direction", (int)capsule2D.direction);
                    break;
                case PolygonCollider2D polygon:
                {
                    Shape2D(json, "polygon2d", polygon);
                    json.Key("paths").BeginArray();
                    for (int path = 0; path < polygon.pathCount; path++)
                    {
                        json.BeginArray();
                        foreach (var point in polygon.GetPath(path))
                            json.Value(Round(point.x)).Value(Round(point.y));
                        json.EndArray();
                    }
                    json.EndArray();
                    break;
                }
                case Collider2D other2D:
                    Shape2D(json, "collider2d", other2D);
                    json.Prop("type", other2D.GetType().Name);
                    Vector(json, "center", other2D.bounds.center);
                    Vector(json, "extents", other2D.bounds.extents);
                    break;
#endif
            }
            json.EndObject();
        }

#if UNITY_PHYSICS_3D
        static void Shape(PartJson json, string kind, Collider collider)
        {
            json.Prop("k", kind);
            if (collider.isTrigger)
                json.Prop("trigger", true);
            if (!collider.enabled)
                json.Prop("disabled", true);
        }

        /// <summary>
        /// A mesh as a blob: <c>PURRMESH</c>, a version byte, the vertex and index counts as two 32-bit numbers,
        /// the positions (three floats each), the indices (16-bit when they fit). Its hash, or null when the mesh
        /// could not be read or is too big to keep.
        /// </summary>
        string MeshBlob(Mesh mesh)
        {
            UnityEngine.Object id = mesh;
            if (_meshHashes.TryGetValue(id, out string known))
                return known;

            string hash = null;
            try
            {
                var vertices = mesh.vertices;
                var triangles = mesh.triangles;
                if (vertices.Length > 0 && triangles.Length > 0 && triangles.Length <= MaxMeshIndices)
                {
                    bool small = vertices.Length <= ushort.MaxValue;
                    using var stream = new MemoryStream(13 + vertices.Length * 12 + triangles.Length * (small ? 2 : 4));
                    using var writer = new BinaryWriter(stream);
                    writer.Write(Encoding.ASCII.GetBytes("PURRMESH"));
                    writer.Write((byte)1);
                    writer.Write(vertices.Length);
                    writer.Write(triangles.Length);
                    for (int i = 0; i < vertices.Length; i++)
                    {
                        writer.Write(vertices[i].x);
                        writer.Write(vertices[i].y);
                        writer.Write(vertices[i].z);
                    }
                    for (int i = 0; i < triangles.Length; i++)
                    {
                        if (small)
                            writer.Write((ushort)triangles[i]);
                        else
                            writer.Write(triangles[i]);
                    }
                    writer.Flush();
                    hash = Keep("mesh", stream.ToArray());
                }
            }
            catch (Exception)
            {
                hash = null;
            }
            _meshHashes[id] = hash;
            return hash;
        }
#endif

#if UNITY_PHYSICS_3D && PURRNET_TERRAIN
        /// <summary>
        /// A terrain's heights as a blob: <c>PURRTERR</c>, a version byte, the resolution as a 32-bit number, then
        /// one 16-bit height per sample, row by row, 65535 being the terrain's full height.
        /// </summary>
        string TerrainBlob(TerrainData data)
        {
            UnityEngine.Object id = data;
            if (_meshHashes.TryGetValue(id, out string known))
                return known;

            string hash = null;
            try
            {
                int resolution = data.heightmapResolution;
                var heights = data.GetHeights(0, 0, resolution, resolution);
                using var stream = new MemoryStream(13 + resolution * resolution * 2);
                using var writer = new BinaryWriter(stream);
                writer.Write(Encoding.ASCII.GetBytes("PURRTERR"));
                writer.Write((byte)1);
                writer.Write(resolution);
                for (int y = 0; y < resolution; y++)
                {
                    for (int x = 0; x < resolution; x++)
                        writer.Write((ushort)Mathf.Clamp(Mathf.RoundToInt(heights[y, x] * 65535f), 0, 65535));
                }
                writer.Flush();
                hash = Keep("terrain", stream.ToArray());
            }
            catch (Exception)
            {
                hash = null;
            }
            _meshHashes[id] = hash;
            return hash;
        }
#endif

#if UNITY_PHYSICS_2D
        static void Shape2D(PartJson json, string kind, Collider2D collider)
        {
            json.Prop("k", kind);
            if (collider.isTrigger)
                json.Prop("trigger", true);
            if (!collider.enabled)
                json.Prop("disabled", true);
            Vector(json, "offset", collider.offset);
        }
#endif

        /// <summary>Keeps a blob under the hash of its bytes and returns that hash.</summary>
        public string Keep(string kind, byte[] data)
        {
            string hash = Hash(data);
            if (blobs.ContainsKey(hash))
                return hash;
            if (blobs.Count >= MaxBlobs)
            {
                truncated = true;
                return null;
            }
            blobs[hash] = (kind, data);
            return hash;
        }

        public static string Hash(byte[] data)
        {
            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(data);
            var text = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes)
                text.Append(b.ToString("x2"));
            return text.ToString();
        }
    }
}
