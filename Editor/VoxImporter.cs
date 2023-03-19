using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEngine;
using UnityEngine.Rendering;

namespace VoxImporter.Editor
{
    [ScriptedImporter(2, "vox")]
    public class VoxImporter : ScriptedImporter
    {
        [SerializeField] private TextureWrapMode wrapMode;
        [SerializeField] private FilterMode filterMode;
        [SerializeField] private int anisoLevel;
        private static readonly int PropNameVoxelTex = Shader.PropertyToID("_VoxelTex");
        private static readonly int PropNameMaxStepsExp = Shader.PropertyToID("_MaxStepsExp");

        public override void OnImportAsset(AssetImportContext ctx)
        {
            var voxelsData = DeserializeVox(ctx.assetPath);
            foreach (var model in voxelsData.Models)
            {
                var volumeTexture = new Texture3D(
                    model.Size.x, model.Size.y, model.Size.z,
                    TextureFormat.RGBAFloat, false);

                var pixels = new Color[model.Size.x * model.Size.y * model.Size.z];
                foreach (var voxel in model.Voxels)
                {
                    var (x, y, z, i) = (voxel.Position.x, voxel.Position.y, voxel.Position.z, voxel.ColorIndex);
                    var index = x + y * model.Size.x + z * model.Size.x * model.Size.y;

                    if (model.Colors != null)
                    {
                        var colorBytes = BitConverter.GetBytes(model.Colors[i]);
                        var (r, g, b, a) =
                            ((float)colorBytes[0], (float)colorBytes[1], (float)colorBytes[2], (float)colorBytes[3]);
                        (r, g, b, a) = (r * 0.0039f, g * 0.0039f, b * 0.0039f, a * 0.0039f);

                        var color = new Color(r, g, b, a);
                        pixels[index] = color;
                    }
                    else
                    {
                        pixels[index] = new Color(1.0f, 1.0f, 1.0f, 1.0f);
                    }
                }

                volumeTexture.SetPixels(pixels);
                volumeTexture.Apply();
                volumeTexture.wrapMode = wrapMode;
                volumeTexture.filterMode = filterMode;
                volumeTexture.anisoLevel = anisoLevel;
                ctx.AddObjectToAsset("model texture", volumeTexture);

                var previewMesh = new Mesh()
                {
                    indexFormat = IndexFormat.UInt16,
                    vertices = new[]
                    {
                        new Vector3(-0.5f, -0.5f, -0.5f), new Vector3(+0.5f, -0.5f, -0.5f),
                        new Vector3(-0.5f, +0.5f, -0.5f), new Vector3(+0.5f, +0.5f, -0.5f),
                        new Vector3(-0.5f, -0.5f, +0.5f), new Vector3(+0.5f, -0.5f, +0.5f),
                        new Vector3(-0.5f, +0.5f, +0.5f), new Vector3(+0.5f, +0.5f, +0.5f),
                    }
                };
                
                previewMesh.SetIndices(new[]
                {
                    0, 2, 3, 1, 4, 5, 7, 6,
                    4, 6, 2, 0, 1, 3, 7, 5,
                    2, 6, 7, 3, 0, 1, 5, 4,
                }, MeshTopology.Quads, 0);
                previewMesh.RecalculateNormals();
                previewMesh.RecalculateBounds();
                previewMesh.Optimize();
                ctx.AddObjectToAsset("model mesh", previewMesh);

                var previewMaterial = new Material(Shader.Find("Lit/VoxLit"));
                previewMaterial.SetTexture(PropNameVoxelTex, volumeTexture);
                previewMaterial.SetFloat(PropNameMaxStepsExp, 7.0f);
                ctx.AddObjectToAsset("model material", previewMaterial);

                var container = new GameObject("model container", typeof(MeshFilter), typeof(MeshRenderer));
                container.GetComponent<MeshFilter>().sharedMesh = previewMesh;
                container.GetComponent<MeshRenderer>().sharedMaterials = new[] { previewMaterial };
                ctx.AddObjectToAsset("model container", container);
            }

            Debug.Log(string.Join("\n", voxelsData.Messages));
        }

        private static VoxelsData DeserializeVox(string path)
        {
            var span = File.ReadAllBytes(path).AsSpan();
            var formatString = Encoding.ASCII.GetString(span[..4]);
            Debug.Assert(formatString == "VOX ", $"format string is not VOX .: {formatString}");

            var versionNumber = BitConverter.ToInt32(span[4..8]);

            var length = span.Length;
            const int cursor = 8;
            Debug.Assert(length > cursor + 12, ".vox should be larger than 20 bytes");

            var voxelsData = new VoxelsData()
            {
                InitializingIndex = -1,
                Messages = new List<string>() { $"version number: {versionNumber}" },
            };
            DeserializeChunk(span[cursor..(cursor + 12)], span[(cursor + 12)..], ref voxelsData);

            return voxelsData;
        }

        private static void DeserializeChunk(Span<byte> spanHeader, Span<byte> spanAfter, ref VoxelsData voxelsData)
        {
            var chunkID = Encoding.ASCII.GetString(spanHeader[..4]);
            var bytesChunkContent = BitConverter.ToInt32(spanHeader[4..8]);
            var bytesChildChunk = BitConverter.ToInt32(spanHeader[8..12]);

            voxelsData.Messages.Add($"chunkID: {chunkID}");
            voxelsData.Messages.Add($"\tcontents size: {bytesChunkContent}");
            voxelsData.Messages.Add($"\tchild size: {bytesChildChunk}");

            switch (chunkID)
            {
                case "MAIN":
                    var mainChunk = DeserializeMainChunk(spanAfter, ref voxelsData);
                    Debug.Assert(mainChunk.ChildChunkSize == bytesChildChunk,
                        $"main chunk child size not as expected: {mainChunk.ChildChunkSize} != {bytesChildChunk}"
                    );
                    break;

                case "PACK":
                    var packChunk = DeserializePackChunk(
                        spanAfter[..bytesChunkContent], spanAfter[bytesChunkContent..], ref voxelsData);
                    Debug.Assert(packChunk != null, "failed to parse pack chunk");
                    break;

                case "SIZE":
                    var sizeChunk = DeserializeSizeChunk(
                        spanAfter[..bytesChunkContent], spanAfter[bytesChunkContent..], ref voxelsData);
                    Debug.Assert(sizeChunk != null, "failed to parse size chunk");
                    break;

                case "XYZI":
                    var xyziChunk = DeserializeXyziChunk(
                        spanAfter[..bytesChunkContent], spanAfter[bytesChunkContent..], ref voxelsData);
                    Debug.Assert(xyziChunk != null, "failed to parse xyzi chunk");
                    break;

                case "RGBA":
                    var rgbaChunk = DeserializeRgbaChunk(
                        spanAfter[..bytesChunkContent], spanAfter[bytesChunkContent..], ref voxelsData);
                    Debug.Assert(rgbaChunk != null, "failed to parse rgba chunk");
                    break;

                case "nTRN":
                    DeserializeTransformChunk(
                        spanAfter[..bytesChunkContent], spanAfter[bytesChunkContent..], ref voxelsData);
                    break;

                case "nGRP":
                    DeserializeGroupChunk(
                        spanAfter[..bytesChunkContent], spanAfter[bytesChunkContent..], ref voxelsData);
                    break;

                case "nSHP":
                    DeserializeShipChunk(
                        spanAfter[..bytesChunkContent], spanAfter[bytesChunkContent..], ref voxelsData);
                    break;

                case "MATL":
                    DeserializeMaterialChunk(
                        spanAfter[..bytesChunkContent], spanAfter[bytesChunkContent..], ref voxelsData);
                    break;

                case "LAYR":
                    DeserializeLayerChunk(
                        spanAfter[..bytesChunkContent], spanAfter[bytesChunkContent..], ref voxelsData);
                    break;

                case "rOBJ":
                    DeserializeObjectChunk(
                        spanAfter[..bytesChunkContent], spanAfter[bytesChunkContent..], ref voxelsData);
                    break;

                case "rCAM":
                    DeserializeCameraChunk(
                        spanAfter[..bytesChunkContent], spanAfter[bytesChunkContent..], ref voxelsData);
                    break;

                case "NOTE":
                    DeserializeNoteChunk(
                        spanAfter[..bytesChunkContent], spanAfter[bytesChunkContent..], ref voxelsData);
                    break;

                case "IMAP":
                    DeserializeIndexMapChunk(
                        spanAfter[..bytesChunkContent], spanAfter[bytesChunkContent..], ref voxelsData);
                    break;

                default:
                    // throw new Exception($"unrecognized chunk id: {chunkID}");
                    Debug.LogError($"unrecognized chunk id: {chunkID}, remaining span length: {spanAfter.Length}");
                    break;
            }
        }

        private static ChunkMain DeserializeMainChunk(Span<byte> data, ref VoxelsData voxelData)
        {
            DeserializeChunk(data[..12], data[12..], ref voxelData);
            return new ChunkMain()
            {
                ChildChunkSize = data.Length,
            };
        }

        private static ChunkPack DeserializePackChunk(Span<byte> span, Span<byte> dataAfter, ref VoxelsData voxelsData)
        {
            var numModels = BitConverter.ToInt32(span[..4]);
            voxelsData.Models = new ModelData[numModels];
            DeserializeChunk(dataAfter[..12], dataAfter[12..], ref voxelsData);
            return new ChunkPack()
            {
                NumModels = numModels,
            };
        }

        private static ChunkSize DeserializeSizeChunk(Span<byte> span, Span<byte> spanAfter, ref VoxelsData voxelsData)
        {
            var sizeX = BitConverter.ToInt32(span[..4]);
            var sizeY = BitConverter.ToInt32(span[4..8]);
            var sizeZ = BitConverter.ToInt32(span[8..12]);

            voxelsData.Models ??= new ModelData[1];
            voxelsData.Models[++voxelsData.InitializingIndex] = new ModelData()
            {
                Size = new Vector3Int(sizeX, sizeY, sizeZ),
            };
            DeserializeChunk(spanAfter[..12], spanAfter[12..], ref voxelsData);

            return new ChunkSize()
            {
                SizeX = sizeX,
                SizeY = sizeY,
                SizeZ = sizeZ,
            };
        }

        private static ChunkXyzI DeserializeXyziChunk(Span<byte> span, Span<byte> dataAfter, ref VoxelsData voxelsData)
        {
            var numVoxels = BitConverter.ToInt32(span[..4]);
            var length = span[4..].Length;
            Debug.Assert(numVoxels * 4 <= length, "span size not enough for numVoxels");

            var model = voxelsData.Models[voxelsData.InitializingIndex];
            model.Voxels = new Voxel[numVoxels];
            for (var index = 0; index < numVoxels; ++index)
            {
                var x = span[4 + index * 4 + 0];
                var y = span[4 + index * 4 + 1];
                var z = span[4 + index * 4 + 2];
                var i = span[4 + index * 4 + 3];
                model.Voxels[index] = new Voxel
                {
                    Position = new Vector3Int(x, z, y),
                    ColorIndex = i,
                };
            }

            DeserializeChunk(dataAfter[..12], dataAfter[12..], ref voxelsData);
            return new ChunkXyzI()
            {
                NumVoxels = numVoxels,
                Voxels = Array.ConvertAll(model.Voxels, (voxel) => voxel.ToInt()),
            };
        }

        private static ChunkRgba DeserializeRgbaChunk(Span<byte> span, Span<byte> dataAfter, ref VoxelsData voxelsData)
        {
            Debug.Log("Colors");
            var model = voxelsData.Models[voxelsData.InitializingIndex];
            // model.Colors ; // TODO

            if (dataAfter.Length > 12) DeserializeChunk(dataAfter[..12], dataAfter[12..], ref voxelsData);
            return new ChunkRgba() { };
        }

        private static void DeserializeTransformChunk(Span<byte> span, Span<byte> dataAfter, ref VoxelsData voxelsData)
        {
            // TODO: don't care at the moment
            if (dataAfter.Length > 12) DeserializeChunk(dataAfter[..12], dataAfter[12..], ref voxelsData);
        }

        private static void DeserializeGroupChunk(Span<byte> span, Span<byte> dataAfter, ref VoxelsData voxelsData)
        {
            // TODO: don't care at the moment
            if (dataAfter.Length > 12) DeserializeChunk(dataAfter[..12], dataAfter[12..], ref voxelsData);
        }

        private static void DeserializeShipChunk(Span<byte> span, Span<byte> dataAfter, ref VoxelsData voxelsData)
        {
            // TODO: don't care at the moment
            if (dataAfter.Length > 12) DeserializeChunk(dataAfter[..12], dataAfter[12..], ref voxelsData);
        }

        private static void DeserializeMaterialChunk(Span<byte> span, Span<byte> dataAfter, ref VoxelsData voxelsData)
        {
            // TODO: don't care at the moment
            if (dataAfter.Length > 12) DeserializeChunk(dataAfter[..12], dataAfter[12..], ref voxelsData);
        }

        private static void DeserializeLayerChunk(Span<byte> span, Span<byte> dataAfter, ref VoxelsData voxelsData)
        {
            // TODO: don't care at the moment
            if (dataAfter.Length > 12) DeserializeChunk(dataAfter[..12], dataAfter[12..], ref voxelsData);
        }

        private static void DeserializeObjectChunk(Span<byte> span, Span<byte> dataAfter, ref VoxelsData voxelsData)
        {
            // TODO: don't care at the moment
            if (dataAfter.Length > 12) DeserializeChunk(dataAfter[..12], dataAfter[12..], ref voxelsData);
        }

        private static void DeserializeCameraChunk(Span<byte> span, Span<byte> dataAfter, ref VoxelsData voxelsData)
        {
            // TODO: don't care at the moment
            if (dataAfter.Length > 12) DeserializeChunk(dataAfter[..12], dataAfter[12..], ref voxelsData);
        }

        private static void DeserializeNoteChunk(Span<byte> span, Span<byte> dataAfter, ref VoxelsData voxelsData)
        {
            // TODO: don't care at the moment
            if (dataAfter.Length > 12) DeserializeChunk(dataAfter[..12], dataAfter[12..], ref voxelsData);
        }

        private static void DeserializeIndexMapChunk(Span<byte> span, Span<byte> dataAfter, ref VoxelsData voxelsData)
        {
            // TODO: don't care at the moment
            if (dataAfter.Length > 12) DeserializeChunk(dataAfter[..12], dataAfter[12..], ref voxelsData);
        }
    }

    internal sealed class VoxelsData
    {
        internal ModelData[] Models;
        internal int InitializingIndex;
        internal List<string> Messages;
    }

    internal sealed class ModelData
    {
        internal Voxel[] Voxels;

        internal uint[] Colors = new uint[256]
        {
            0x00000000, 0xffffffff, 0xffccffff, 0xff99ffff, 0xff66ffff, 0xff33ffff, 0xff00ffff, 0xffffccff, 0xffccccff,
            0xff99ccff, 0xff66ccff, 0xff33ccff, 0xff00ccff, 0xffff99ff, 0xffcc99ff, 0xff9999ff,
            0xff6699ff, 0xff3399ff, 0xff0099ff, 0xffff66ff, 0xffcc66ff, 0xff9966ff, 0xff6666ff, 0xff3366ff, 0xff0066ff,
            0xffff33ff, 0xffcc33ff, 0xff9933ff, 0xff6633ff, 0xff3333ff, 0xff0033ff, 0xffff00ff,
            0xffcc00ff, 0xff9900ff, 0xff6600ff, 0xff3300ff, 0xff0000ff, 0xffffffcc, 0xffccffcc, 0xff99ffcc, 0xff66ffcc,
            0xff33ffcc, 0xff00ffcc, 0xffffcccc, 0xffcccccc, 0xff99cccc, 0xff66cccc, 0xff33cccc,
            0xff00cccc, 0xffff99cc, 0xffcc99cc, 0xff9999cc, 0xff6699cc, 0xff3399cc, 0xff0099cc, 0xffff66cc, 0xffcc66cc,
            0xff9966cc, 0xff6666cc, 0xff3366cc, 0xff0066cc, 0xffff33cc, 0xffcc33cc, 0xff9933cc,
            0xff6633cc, 0xff3333cc, 0xff0033cc, 0xffff00cc, 0xffcc00cc, 0xff9900cc, 0xff6600cc, 0xff3300cc, 0xff0000cc,
            0xffffff99, 0xffccff99, 0xff99ff99, 0xff66ff99, 0xff33ff99, 0xff00ff99, 0xffffcc99,
            0xffcccc99, 0xff99cc99, 0xff66cc99, 0xff33cc99, 0xff00cc99, 0xffff9999, 0xffcc9999, 0xff999999, 0xff669999,
            0xff339999, 0xff009999, 0xffff6699, 0xffcc6699, 0xff996699, 0xff666699, 0xff336699,
            0xff006699, 0xffff3399, 0xffcc3399, 0xff993399, 0xff663399, 0xff333399, 0xff003399, 0xffff0099, 0xffcc0099,
            0xff990099, 0xff660099, 0xff330099, 0xff000099, 0xffffff66, 0xffccff66, 0xff99ff66,
            0xff66ff66, 0xff33ff66, 0xff00ff66, 0xffffcc66, 0xffcccc66, 0xff99cc66, 0xff66cc66, 0xff33cc66, 0xff00cc66,
            0xffff9966, 0xffcc9966, 0xff999966, 0xff669966, 0xff339966, 0xff009966, 0xffff6666,
            0xffcc6666, 0xff996666, 0xff666666, 0xff336666, 0xff006666, 0xffff3366, 0xffcc3366, 0xff993366, 0xff663366,
            0xff333366, 0xff003366, 0xffff0066, 0xffcc0066, 0xff990066, 0xff660066, 0xff330066,
            0xff000066, 0xffffff33, 0xffccff33, 0xff99ff33, 0xff66ff33, 0xff33ff33, 0xff00ff33, 0xffffcc33, 0xffcccc33,
            0xff99cc33, 0xff66cc33, 0xff33cc33, 0xff00cc33, 0xffff9933, 0xffcc9933, 0xff999933,
            0xff669933, 0xff339933, 0xff009933, 0xffff6633, 0xffcc6633, 0xff996633, 0xff666633, 0xff336633, 0xff006633,
            0xffff3333, 0xffcc3333, 0xff993333, 0xff663333, 0xff333333, 0xff003333, 0xffff0033,
            0xffcc0033, 0xff990033, 0xff660033, 0xff330033, 0xff000033, 0xffffff00, 0xffccff00, 0xff99ff00, 0xff66ff00,
            0xff33ff00, 0xff00ff00, 0xffffcc00, 0xffcccc00, 0xff99cc00, 0xff66cc00, 0xff33cc00,
            0xff00cc00, 0xffff9900, 0xffcc9900, 0xff999900, 0xff669900, 0xff339900, 0xff009900, 0xffff6600, 0xffcc6600,
            0xff996600, 0xff666600, 0xff336600, 0xff006600, 0xffff3300, 0xffcc3300, 0xff993300,
            0xff663300, 0xff333300, 0xff003300, 0xffff0000, 0xffcc0000, 0xff990000, 0xff660000, 0xff330000, 0xff0000ee,
            0xff0000dd, 0xff0000bb, 0xff0000aa, 0xff000088, 0xff000077, 0xff000055, 0xff000044,
            0xff000022, 0xff000011, 0xff00ee00, 0xff00dd00, 0xff00bb00, 0xff00aa00, 0xff008800, 0xff007700, 0xff005500,
            0xff004400, 0xff002200, 0xff001100, 0xffee0000, 0xffdd0000, 0xffbb0000, 0xffaa0000,
            0xff880000, 0xff770000, 0xff550000, 0xff440000, 0xff220000, 0xff110000, 0xffeeeeee, 0xffdddddd, 0xffbbbbbb,
            0xffaaaaaa, 0xff888888, 0xff777777, 0xff555555, 0xff444444, 0xff222222, 0xff111111
        };

        internal Vector3Int Size;
    }

    internal sealed class Voxel
    {
        internal Vector3Int Position;
        internal int ColorIndex;

        public int ToInt()
        {
            var x = (byte)Position.x;
            var y = (byte)Position.y;
            var z = (byte)Position.z;
            var i = (byte)ColorIndex;
            return BitConverter.ToInt32(new byte[] { x, y, z, i });
        }
    }

    internal sealed class ChunkMain
    {
        internal int ChildChunkSize;
    }

    internal sealed class ChunkPack
    {
        internal int NumModels;
    }

    internal sealed class ChunkSize
    {
        internal int SizeX;
        internal int SizeY;
        internal int SizeZ;
    }

    internal sealed class ChunkXyzI
    {
        internal int NumVoxels;
        internal int[] Voxels;
    }

    internal sealed class ChunkRgba
    {
        internal int[] Colors;
    }

    [CustomEditor(typeof(VoxImporter))]
    internal class VoxImporterEditor : ScriptedImporterEditor
    {
        private SerializedProperty _propWrapMode;
        private SerializedProperty _propFilterMode;
        private SerializedProperty _propAnisoLevel;

        public override void OnEnable()
        {
            base.OnEnable();
            _propWrapMode = serializedObject.FindProperty("wrapMode");
            _propFilterMode = serializedObject.FindProperty("filterMode");
            _propAnisoLevel = serializedObject.FindProperty("anisoLevel");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            if (_propWrapMode != null) EditorGUILayout.PropertyField(_propWrapMode);
            if (_propFilterMode != null) EditorGUILayout.PropertyField(_propFilterMode);
            if (_propAnisoLevel != null) EditorGUILayout.PropertyField(_propAnisoLevel);

            serializedObject.ApplyModifiedProperties();
            ApplyRevertGUI();
        }
    }
}