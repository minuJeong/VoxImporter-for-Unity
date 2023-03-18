using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEngine;
using UnityEngine.UIElements;

namespace VoxImporter.Editor
{
    [ScriptedImporter(1, "vox")]
    public class VoxImporter : ScriptedImporter
    {
        [SerializeField] private TextureWrapMode wrapMode;
        [SerializeField] private FilterMode filterMode;
        [SerializeField] private int anisoLevel;
        
        public override void OnImportAsset(AssetImportContext ctx)
        {
            var voxelsData = DeserializeVox(ctx.assetPath);
            Debug.Log(string.Join("\n", voxelsData.Messages));

            foreach (var model in voxelsData.Models)
            {
                var volumeTexture = new Texture3D(
                    model.Size.x, model.Size.y, model.Size.z,
                    TextureFormat.RGBAFloat, false);

                var colors = new Color[model.Size.x * model.Size.y * model.Size.z];
                foreach (var voxel in model.Voxels)
                {
                    var (x, y, z, i) = (voxel.Position.x, voxel.Position.y, voxel.Position.z, voxel.ColorIndex);
                    var colorBytes = BitConverter.GetBytes(model.Colors[i]);
                    var (r, g, b, a) =
                        ((int)colorBytes[0], (int)colorBytes[1], (int)colorBytes[2], (int)colorBytes[3]);
                    var color = new Color(r / 255.0f, g / 255.0f, b / 255.0f, 1.0f);
                    var index = x + y * model.Size.x + z * model.Size.x * model.Size.y;
                    // colors[index] = color;
                    colors[index] = new Color(1, 1, 1, 1);
                }

                volumeTexture.SetPixels(colors);
                volumeTexture.Apply();
                volumeTexture.wrapMode = wrapMode;
                volumeTexture.filterMode = filterMode;
                volumeTexture.anisoLevel = anisoLevel;
                ctx.AddObjectToAsset("vox_texture", volumeTexture);
            }
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

                default:
                    // throw new Exception($"unrecognized chunk id: {chunkID}");
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
            model.Colors = new int[numVoxels];
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
                // Voxels = model.Voxels,
            };
        }

        private static ChunkRgba DeserializeRgbaChunk(Span<byte> span, Span<byte> dataAfter, ref VoxelsData voxelsData)
        {
            return new ChunkRgba() { };
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
        internal int[] Colors;
        internal Vector3Int Size;
    }

    internal sealed class Voxel
    {
        internal Vector3Int Position;
        internal int ColorIndex;
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

            if (_propWrapMode != null)  EditorGUILayout.PropertyField(_propWrapMode);
            if (_propFilterMode != null) EditorGUILayout.PropertyField(_propFilterMode);
            if (_propAnisoLevel != null) EditorGUILayout.PropertyField(_propAnisoLevel);

            serializedObject.ApplyModifiedProperties();
            ApplyRevertGUI();
        }
    }
}