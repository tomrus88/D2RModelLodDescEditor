using System.Buffers.Binary;

namespace Granny.Format;

/// <summary>
/// Field offsets of the granny structures a D2R grn file stores, taken from
/// <c>bg3_Release.exe</c>, which statically links granny with full symbols, and
/// then validated byte-for-byte against real assets.
/// </summary>
/// <remarks>
/// <para>
/// The offsets come from the granny structs IDA recovers from
/// <c>bg3_Release.exe</c> (full granny symbols; <c>granny_file_info</c> ord.
/// 54319, <c>granny_tri_topology</c> ord. 54291, <c>granny_mesh</c> ord.
/// 54294, <c>granny_bone_binding</c> ord. 54293, <c>granny_vertex_data</c>
/// ord. 54287). bg3 ships the same granny version as D2R. The layout was
/// confirmed against <c>torso_lod0.model</c> and
/// <c>special1_rol_lod0.model</c>: every pointer resolves to a real object,
/// mesh names decode as text, and
/// <c>group.TriCount * 3 == triTopology.Index16Count</c> holds exactly.
/// </para>
/// <para>
/// Note these are the 64-bit member offsets even though the file stores 4-byte
/// pointers: granny keeps its field layout and simply narrows the relocated
/// slots. Reading the file with 32-bit (compacted) offsets lands every field
/// in the wrong place.
/// </para>
/// </remarks>
public static class GrannyLayout
{
    // granny_file_info
    public const int FileFromFileName = 16;
    public const int FileTextureCount = 24;
    public const int FileMaterialCount = 36;
    public const int FileSkeletonCount = 48;
    public const int FileVertexDataCount = 60;
    public const int FileTriTopologyCount = 72;
    public const int FileMeshCount = 84;
    public const int FileMeshes = 88;
    public const int FileModelCount = 96;

    // granny_mesh
    public const int MeshName = 0;
    public const int MeshPrimaryVertexData = 8;
    public const int MeshMorphTargetCount = 16;
    public const int MeshMorphTargets = 20;
    public const int MeshPrimaryTopology = 28;
    public const int MeshMaterialBindingCount = 36;
    public const int MeshMaterialBindings = 40;
    public const int MeshBoneBindingCount = 48;
    public const int MeshBoneBindings = 52;

    // granny_vertex_data
    public const int VertexDataVertexType = 0;
    public const int VertexDataVertexCount = 8;
    public const int VertexDataVertices = 12;

    // granny_tri_topology
    public const int TopologyGroupCount = 0;
    public const int TopologyGroups = 4;
    public const int TopologyIndexCount = 12;
    public const int TopologyIndices = 16;
    public const int TopologyIndex16Count = 24;
    public const int TopologyIndices16 = 28;

    // granny_tri_material_group
    public const int GroupMaterialIndex = 0;
    public const int GroupTriFirst = 4;
    public const int GroupTriCount = 8;
    public const int TriMaterialGroupSize = 12;

    // granny_bone_binding
    public const int BoneBindingBoneName = 0;
    public const int BoneBindingObbMin = 8;
    public const int BoneBindingObbMax = 20;
    public const int BoneBindingTriangleCount = 32;
    public const int BoneBindingTriangleIndices = 36;
    public const int BoneBindingSize = 44;

    /// <summary>Bytes per D2R vertex record.</summary>
    /// <remarks>
    /// Recovered from the <c>granny_data_type_definition</c> component list that
    /// <c>granny_vertex_data.VertexType</c> points at: four int16
    /// (binormal/position), four uint8 (normal), four uint8, four int8, four
    /// int8, and two half floats. 4*2 + 4 + 4 + 4 + 4 + 4 = 28.
    /// </remarks>
    public const int VertexStride = 28;

    /// <summary>Quantisation step of the packed position component.</summary>
    public const float PositionScale = 1.0f / 4096.0f;

    /// <summary>Component order of the packed position: y, x, z.</summary>
    /// <remarks>
    /// The packed position is the standard granny <c>BinormalInt16</c> x4
    /// component; granny's own <c>GrannyDecodeBinormalInt16x4</c> writes the
    /// four decoded values in stored order. The stored order is therefore
    /// (x, y, z) and no permutation is applied.
    /// </remarks>
    public static readonly int[] PositionComponentOrder = [0, 1, 2];
}
