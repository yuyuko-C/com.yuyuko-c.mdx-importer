

using MdxLib.Animator;
using MdxLib.Model;
using MdxLib.ModelFormats;
using MdxLib.Primitives;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

public class MyMdxModel
{
    private string m_Path;
    private CModel m_CModel;
    // Bones reference geosets with geoanims, if there aren't geoanims, then bones will act as helpers.
    private CObjectContainer<CBone> m_CBones;
    // Helpers are only used for doing transformations to their children.
    private CObjectContainer<CHelper> m_CHelpers;
    private SortedDictionary<int, GameObject> m_AllBones;
    private List<CGeoset> m_AllGeosets = new List<CGeoset>();

    public GameObject SkeletonRoot => m_SkeletonRoot;
    public IEnumerable<Transform> BonesTransforms => m_AllBones.Select(x => x.Value.transform);

    private GameObject m_SkeletonRoot;


    public MyMdxModel(string filePath)
    {
        m_Path = filePath;
        m_CModel = ReadModelFromFile();
        m_SkeletonRoot = new GameObject("Skeleton");
        m_AllBones = new SortedDictionary<int, GameObject>();
        m_AllGeosets = new List<CGeoset>();

        InitializeModelBones();
        InitializeBonesRelation();
        InitializeAttachments();
        InitializeEvents();
        InitializeParticleEmitters();
        InitializeCollisionShapes();
    }

    public SkinnedMeshRenderer GetSkinnedMeshRenderer()
    {
        SkinnedMeshRenderer skinnedMeshRenderer = new SkinnedMeshRenderer();


        return skinnedMeshRenderer;
    }

    public Mesh GetMesh(List<int> excludeGeosetIds, List<string> excludeTexture, GameObject gameObject)
    {
        Mesh mesh = new Mesh();
        mesh.name = Path.GetFileNameWithoutExtension(m_Path);

        // Set the bounding box.
        Bounds bounds = new Bounds();
        bounds.min = m_CModel.Extent.Min.ToVector3().SwapYZ();
        bounds.max = m_CModel.Extent.Max.ToVector3().SwapYZ();
        mesh.bounds = bounds;

        // For each geoset.
        List<CombineInstance> combines = new List<CombineInstance>();
        for (int i = 0; i < m_CModel.Geosets.Count; i++)
        {
            CGeoset cgeoset = m_CModel.Geosets.Get(i);
            if (excludeGeosetIds.Contains(i) || cgeoset.ContainsTextures(excludeTexture) || !cgeoset.HasReferences)
            {
                continue;
            }
            CombineInstance combine = GetCombineInstance(cgeoset);
            combines.Add(combine);
            m_AllGeosets.Add(cgeoset);
        }

        // Combine the submeshes.
        // This operation removes vertices that don't belong to any triangle.
        mesh.CombineMeshes(combines.ToArray(), false);

        mesh.boneWeights = GetBoneWeights().ToArray();

        // Calculate bind pose.
        // The bind pose is the inverse of the transformation matrix of the bone when the bone is in the bind pose.
        mesh.bindposes = BonesTransforms.Select(x => x.worldToLocalMatrix * gameObject.transform.localToWorldMatrix).ToArray();

        return mesh;
    }

    public List<Material> GetMaterials()
    {
        // Add the materials to the renderer in order.
        List<Material> rendererMaterials = new List<Material>();

        foreach (var item in m_AllGeosets)
        {
            Material material = GetUnityMaterial(item.Material.Object);
            material.name = Path.GetFileNameWithoutExtension(m_Path) + rendererMaterials.Count.ToString();
            if (!rendererMaterials.Contains(material))
            {
                rendererMaterials.Add(material);
            }
        }

        return rendererMaterials;
    }

    public List<AnimationClip> GetAnimationClips(float frameRate, bool importTangents, List<string> excludeAnimName)
    {
        List<AnimationClip> animationClips = new List<AnimationClip>();

        // For each sequence.
        for (int i = 0; i < m_CModel.Sequences.Count; i++)
        {
            CSequence csequence = m_CModel.Sequences.Get(i);
            if (excludeAnimName.Contains(csequence.Name))
            {
                continue;
            }

            AnimationClip clip = new AnimationClip();
            clip.name = csequence.Name;

            // Set the loop mode.
            if (!csequence.NonLooping)
            {
                clip.wrapMode = WrapMode.Loop;
            }

            // For each bone.
            for (int j = 0; j < m_CModel.Bones.Count; j++)
            {
                CBone cbone = m_CModel.Bones.Get(j);
                GameObject bone = m_AllBones[cbone.NodeId];
                string path = GetPath(bone);

                // Translation.
                {
                    AnimationCurve curveX = new AnimationCurve();
                    AnimationCurve curveY = new AnimationCurve();
                    AnimationCurve curveZ = new AnimationCurve();

                    CAnimator<CVector3> ctranslations = cbone.Translation;
                    for (int k = 0; k < ctranslations.Count; k++)
                    {
                        CAnimatorNode<CVector3> node = ctranslations.Get(k);
                        if (csequence.IntervalStart <= node.Time && node.Time <= csequence.IntervalEnd)
                        {
                            float time = node.Time - csequence.IntervalStart;
                            Vector3 position = bone.transform.localPosition + node.Value.ToVector3().SwapYZ();

                            Keyframe keyX = new Keyframe(time / frameRate, position.x);
                            if (importTangents)
                            {
                                keyX.inTangent = node.InTangent.X;
                                keyX.outTangent = node.OutTangent.X;
                            }
                            curveX.AddKey(keyX);

                            Keyframe keyY = new Keyframe(time / frameRate, position.y);
                            if (importTangents)
                            {
                                keyY.inTangent = node.InTangent.Z;
                                keyY.outTangent = node.OutTangent.Z;
                            }
                            curveY.AddKey(keyY);

                            Keyframe keyZ = new Keyframe(time / frameRate, position.z);
                            if (importTangents)
                            {
                                keyZ.inTangent = node.InTangent.Y;
                                keyZ.outTangent = node.OutTangent.Y;
                            }
                            curveZ.AddKey(keyZ);
                        }
                    }

                    if (curveX.length > 0)
                    {
                        clip.SetCurve(path, typeof(Transform), "localPosition.x", curveX);
                    }
                    if (curveY.length > 0)
                    {
                        clip.SetCurve(path, typeof(Transform), "localPosition.y", curveY);
                    }
                    if (curveZ.length > 0)
                    {
                        clip.SetCurve(path, typeof(Transform), "localPosition.z", curveZ);
                    }
                }

                // Rotation.
                {
                    AnimationCurve curveX = new AnimationCurve();
                    AnimationCurve curveY = new AnimationCurve();
                    AnimationCurve curveZ = new AnimationCurve();
                    AnimationCurve curveW = new AnimationCurve();

                    CAnimator<CVector4> crotations = cbone.Rotation;
                    for (int k = 0; k < crotations.Count; k++)
                    {
                        CAnimatorNode<CVector4> node = crotations.Get(k);
                        if (csequence.IntervalStart <= node.Time && node.Time <= csequence.IntervalEnd)
                        {
                            float time = node.Time - csequence.IntervalStart;
                            Quaternion rotation = node.Value.ToQuaternion();

                            Keyframe keyX = new Keyframe(time / frameRate, rotation.x);
                            if (importTangents)
                            {
                                keyX.inTangent = node.InTangent.X;
                                keyX.outTangent = node.OutTangent.X;
                            }
                            curveX.AddKey(keyX);

                            Keyframe keyY = new Keyframe(time / frameRate, rotation.z);
                            if (importTangents)
                            {
                                keyY.inTangent = node.InTangent.Z;
                                keyY.outTangent = node.OutTangent.Z;
                            }
                            curveY.AddKey(keyY);

                            Keyframe keyZ = new Keyframe(time / frameRate, rotation.y);
                            if (importTangents)
                            {
                                keyZ.inTangent = node.InTangent.Y;
                                keyZ.outTangent = node.OutTangent.Y;
                            }
                            curveZ.AddKey(keyZ);

                            Keyframe keyW = new Keyframe(time / frameRate, -rotation.w);
                            if (importTangents)
                            {
                                keyW.inTangent = node.InTangent.W;
                                keyW.outTangent = node.OutTangent.W;
                            }
                            curveW.AddKey(keyW);
                        }
                    }

                    if (curveX.length > 0)
                    {
                        clip.SetCurve(path, typeof(Transform), "localRotation.x", curveX);
                    }
                    if (curveY.length > 0)
                    {
                        clip.SetCurve(path, typeof(Transform), "localRotation.y", curveY);
                    }
                    if (curveZ.length > 0)
                    {
                        clip.SetCurve(path, typeof(Transform), "localRotation.z", curveZ);
                    }
                    if (curveW.length > 0)
                    {
                        clip.SetCurve(path, typeof(Transform), "localRotation.w", curveW);
                    }
                }

                // Scaling.
                {
                    AnimationCurve curveX = new AnimationCurve();
                    AnimationCurve curveY = new AnimationCurve();
                    AnimationCurve curveZ = new AnimationCurve();

                    CAnimator<CVector3> cscalings = cbone.Scaling;
                    for (int k = 0; k < cscalings.Count; k++)
                    {
                        CAnimatorNode<CVector3> node = cscalings.Get(k);
                        if (csequence.IntervalStart <= node.Time && node.Time <= csequence.IntervalEnd)
                        {
                            float time = node.Time - csequence.IntervalStart;

                            Keyframe keyX = new Keyframe(time / frameRate, node.Value.X);
                            curveX.AddKey(keyX);

                            Keyframe keyY = new Keyframe(time / frameRate, node.Value.Z);
                            curveY.AddKey(keyY);

                            Keyframe keyZ = new Keyframe(time / frameRate, node.Value.Y);
                            curveZ.AddKey(keyZ);
                        }
                    }

                    if (curveX.length > 0)
                    {
                        clip.SetCurve(path, typeof(Transform), "localScale.x", curveX);
                    }
                    if (curveY.length > 0)
                    {
                        clip.SetCurve(path, typeof(Transform), "localScale.y", curveY);
                    }
                    if (curveZ.length > 0)
                    {
                        clip.SetCurve(path, typeof(Transform), "localScale.z", curveZ);
                    }
                }
            }

            // For each helper.
            for (int j = 0; j < m_CModel.Helpers.Count; j++)
            {
                CHelper chelper = m_CModel.Helpers.Get(j);
                GameObject bone = m_AllBones[chelper.NodeId];
                string path = GetPath(bone);

                // Translation.
                {
                    AnimationCurve curveX = new AnimationCurve();
                    AnimationCurve curveY = new AnimationCurve();
                    AnimationCurve curveZ = new AnimationCurve();

                    CAnimator<CVector3> ctranslations = chelper.Translation;
                    for (int k = 0; k < ctranslations.Count; k++)
                    {
                        CAnimatorNode<CVector3> node = ctranslations.Get(k);
                        if (csequence.IntervalStart <= node.Time && node.Time <= csequence.IntervalEnd)
                        {
                            float time = node.Time - csequence.IntervalStart;
                            Vector3 position = bone.transform.localPosition + node.Value.ToVector3().SwapYZ();

                            Keyframe keyX = new Keyframe(time / frameRate, position.x);
                            if (importTangents)
                            {
                                keyX.inTangent = node.InTangent.X;
                                keyX.outTangent = node.OutTangent.X;
                            }
                            curveX.AddKey(keyX);

                            Keyframe keyY = new Keyframe(time / frameRate, position.y);
                            if (importTangents)
                            {
                                keyY.inTangent = node.InTangent.Z;
                                keyY.outTangent = node.OutTangent.Z;
                            }
                            curveY.AddKey(keyY);

                            Keyframe keyZ = new Keyframe(time / frameRate, position.z);
                            if (importTangents)
                            {
                                keyZ.inTangent = node.InTangent.Y;
                                keyZ.outTangent = node.OutTangent.Y;
                            }
                            curveZ.AddKey(keyZ);
                        }
                    }

                    if (curveX.length > 0)
                    {
                        clip.SetCurve(path, typeof(Transform), "localPosition.x", curveX);
                    }
                    if (curveY.length > 0)
                    {
                        clip.SetCurve(path, typeof(Transform), "localPosition.y", curveY);
                    }
                    if (curveZ.length > 0)
                    {
                        clip.SetCurve(path, typeof(Transform), "localPosition.z", curveZ);
                    }
                }

                // Rotation.
                {
                    AnimationCurve curveX = new AnimationCurve();
                    AnimationCurve curveY = new AnimationCurve();
                    AnimationCurve curveZ = new AnimationCurve();
                    AnimationCurve curveW = new AnimationCurve();

                    CAnimator<CVector4> crotations = chelper.Rotation;
                    for (int k = 0; k < crotations.Count; k++)
                    {
                        CAnimatorNode<CVector4> node = crotations.Get(k);
                        if (csequence.IntervalStart <= node.Time && node.Time <= csequence.IntervalEnd)
                        {
                            float time = node.Time - csequence.IntervalStart;
                            Quaternion rotation = node.Value.ToQuaternion();

                            Keyframe keyX = new Keyframe(time / frameRate, rotation.x);
                            if (importTangents)
                            {
                                keyX.inTangent = node.InTangent.X;
                                keyX.outTangent = node.OutTangent.X;
                            }
                            curveX.AddKey(keyX);

                            Keyframe keyY = new Keyframe(time / frameRate, rotation.z);
                            if (importTangents)
                            {
                                keyY.inTangent = node.InTangent.Z;
                                keyY.outTangent = node.OutTangent.Z;
                            }
                            curveY.AddKey(keyY);

                            Keyframe keyZ = new Keyframe(time / frameRate, rotation.y);
                            if (importTangents)
                            {
                                keyZ.inTangent = node.InTangent.Y;
                                keyZ.outTangent = node.OutTangent.Y;
                            }
                            curveZ.AddKey(keyZ);

                            Keyframe keyW = new Keyframe(time / frameRate, -rotation.w);
                            if (importTangents)
                            {
                                keyW.inTangent = node.InTangent.W;
                                keyW.outTangent = node.OutTangent.W;
                            }
                            curveW.AddKey(keyW);
                        }
                    }

                    if (curveX.length > 0)
                    {
                        clip.SetCurve(path, typeof(Transform), "localRotation.x", curveX);
                    }
                    if (curveY.length > 0)
                    {
                        clip.SetCurve(path, typeof(Transform), "localRotation.y", curveY);
                    }
                    if (curveZ.length > 0)
                    {
                        clip.SetCurve(path, typeof(Transform), "localRotation.z", curveZ);
                    }
                    if (curveW.length > 0)
                    {
                        clip.SetCurve(path, typeof(Transform), "localRotation.w", curveW);
                    }
                }

                // Scaling.
                {
                    AnimationCurve curveX = new AnimationCurve();
                    AnimationCurve curveY = new AnimationCurve();
                    AnimationCurve curveZ = new AnimationCurve();

                    CAnimator<CVector3> cscalings = chelper.Scaling;
                    for (int k = 0; k < cscalings.Count; k++)
                    {
                        CAnimatorNode<CVector3> node = cscalings.Get(k);
                        if (csequence.IntervalStart <= node.Time && node.Time <= csequence.IntervalEnd)
                        {
                            float time = node.Time - csequence.IntervalStart;

                            Keyframe keyX = new Keyframe(time / frameRate, node.Value.X);
                            curveX.AddKey(keyX);

                            Keyframe keyY = new Keyframe(time / frameRate, node.Value.Z);
                            curveY.AddKey(keyY);

                            Keyframe keyZ = new Keyframe(time / frameRate, node.Value.Y);
                            curveZ.AddKey(keyZ);
                        }
                    }

                    if (curveX.length > 0)
                    {
                        clip.SetCurve(path, typeof(Transform), "localScale.x", curveX);
                    }
                    if (curveY.length > 0)
                    {
                        clip.SetCurve(path, typeof(Transform), "localScale.y", curveY);
                    }
                    if (curveZ.length > 0)
                    {
                        clip.SetCurve(path, typeof(Transform), "localScale.z", curveZ);
                    }
                }
            }

            // Realigns quaternion keys to ensure shortest interpolation paths and avoid rotation glitches.
            clip.EnsureQuaternionContinuity();

            animationClips.Add(clip);
        }

        return animationClips;
    }

    private Material GetUnityMaterial(CMaterial cmaterial)
    {
        Material material = new Material(Shader.Find("MDX/Standard"));

        // For each layer.
        int blendMode = 1; // Cutout.
        bool twoSided = false;
        for (int j = 0; j < cmaterial.Layers.Count; j++)
        {
            CMaterialLayer clayer = cmaterial.Layers[j];

            // Two Sided.
            if (clayer.TwoSided)
            {
                twoSided = true;
            }

            // Team color.
            if (clayer?.Texture?.Object.ReplaceableId > 0)
            {
                blendMode = 0; // Opaque.
            }
        }

        material.SetFloat("_Cutoff", 0.5f);
        material.SetInt("_Cull", (twoSided) ? (int)UnityEngine.Rendering.CullMode.Off : (int)UnityEngine.Rendering.CullMode.Back);
        material.SetFloat("_Mode", blendMode);

        Debug.Log(material.name);


        return material;
    }

    private List<BoneWeight> GetBoneWeights()
    {
        List<BoneWeight> weights = new List<BoneWeight>();
        foreach (var cgeoset in m_AllGeosets)
        {
            CObjectContainer<CGeosetVertex> cvertices = cgeoset.Vertices;
            for (int j = 0; j < cvertices.Count; j++)
            {
                CGeosetVertex cvertex = cvertices.Get(j);

                // Check if the vertex belongs to a triangle.
                // Mesh combines discard vertices that don't belong to any triangle. To avoid the error "Mesh.boneWeights is out of bounds" (more weights than vertices), these weights are ignored.
                bool hasTriangle = false;
                foreach (CGeosetFace cface in cgeoset.Faces)
                {
                    if (cvertex.ObjectId == cface.Vertex1.ObjectId || cvertex.ObjectId == cface.Vertex2.ObjectId || cvertex.ObjectId == cface.Vertex3.ObjectId)
                    {
                        hasTriangle = true;
                        break;
                    }
                }
                if (!hasTriangle)
                {
                    continue;
                }

                BoneWeight weight = new BoneWeight();

                // Group.
                // A vertex group reference a group (of matrices).
                CGeosetGroup cgroup = cvertex.Group.Object; // Vertex group reference.

                // Matrices.
                // A matrix reference an object. The bone weights are evenly distributed, each weight is 1/N.
                CObjectContainer<CGeosetGroupNode> cmatrices = cgroup.Nodes;
                for (int k = 0; k < cmatrices.Count; k++)
                {
                    CGeosetGroupNode cmatrix = cmatrices.Get(k);
                    switch (k)
                    {
                        case 0:
                            {
                                weight.boneIndex0 = cmatrix.Node.NodeId;
                                weight.weight0 = 1f / cmatrices.Count;
                                break;
                            }
                        case 1:
                            {
                                weight.boneIndex1 = cmatrix.Node.NodeId;
                                weight.weight1 = 1f / cmatrices.Count;
                                break;
                            }
                        case 2:
                            {
                                weight.boneIndex2 = cmatrix.Node.NodeId;
                                weight.weight2 = 1f / cmatrices.Count;
                                break;
                            }
                        case 3:
                            {
                                weight.boneIndex3 = cmatrix.Node.NodeId;
                                weight.weight3 = 1f / cmatrices.Count;
                                break;
                            }
                            //default:
                            //{
                            //    throw new Exception("Invalid number of bones " + k + " when skining.");
                            //}
                    }
                }

                weights.Add(weight);
            }
        }
        return weights;
    }

    private CombineInstance GetCombineInstance(CGeoset cgeoset)
    {
        CombineInstance combine = new CombineInstance();
        Mesh submesh = new Mesh();

        // Vertices.
        List<Vector3> vertices = new List<Vector3>();
        for (int j = 0; j < cgeoset.Vertices.Count; j++)
        {
            // MDX/MDL up axis is Z.
            // Unity up axis is Y.
            CGeosetVertex cvertex = cgeoset.Vertices.Get(j);
            Vector3 vertex = new Vector3(cvertex.Position.X, cvertex.Position.Z, cvertex.Position.Y);
            vertices.Add(vertex);
        }

        // Triangles.
        List<int> triangles = new List<int>();
        for (int j = 0; j < cgeoset.Faces.Count; j++)
        {
            // MDX/MDL coordinate system is anti-clockwise.
            // Unity coordinate system is clockwise.
            CGeosetFace cface = cgeoset.Faces.Get(j);
            triangles.Add(cface.Vertex1.ObjectId);
            triangles.Add(cface.Vertex3.ObjectId); // Swap the order of the vertex 2 and 3.
            triangles.Add(cface.Vertex2.ObjectId);
        }

        // Normals.
        List<Vector3> normals = new List<Vector3>();
        for (int j = 0; j < cgeoset.Vertices.Count; j++)
        {
            // MDX/MDL up axis is Z.
            // Unity up axis is Y.
            CGeosetVertex cvertex = cgeoset.Vertices.Get(j);
            Vector3 normal = new Vector3(cvertex.Normal.X, cvertex.Normal.Z, cvertex.Normal.Y);
            normals.Add(normal);
        }

        // UVs.
        List<Vector2> uvs = new List<Vector2>();
        for (int j = 0; j < cgeoset.Vertices.Count; j++)
        {
            // MDX/MDL texture coordinate origin is at top left.
            // Unity texture coordinate origin is at bottom left.
            CGeosetVertex cvertex = cgeoset.Vertices.Get(j);
            Vector2 uv = new Vector2(cvertex.TexturePosition.X, Mathf.Abs(cvertex.TexturePosition.Y - 1)); // Vunity = abs(Vmdx - 1)
            uvs.Add(uv);
        }

        submesh.vertices = vertices.ToArray();
        submesh.triangles = triangles.ToArray();
        submesh.normals = normals.ToArray();
        submesh.uv = uvs.ToArray();

        combine.mesh = submesh;
        combine.transform = Matrix4x4.identity;

        return combine;
    }

    private void InitializeAttachments()
    {
        CObjectContainer<CAttachment> cattachments = m_CModel.Attachments;
        for (int i = 0; i < cattachments.Count; i++)
        {
            CAttachment cattachment = cattachments.Get(i);
            CreateGameObject(cattachment.Name, cattachment.PivotPoint, cattachment.Parent.NodeId);
        }
    }

    private void InitializeModelBones()
    {
        for (int i = 0; i < m_CBones.Count; i++)
        {
            CBone cbone = m_CBones.Get(i);
            GameObject bone = Convert(cbone);
            m_AllBones[cbone.NodeId] = bone;
        }

        for (int i = 0; i < m_CHelpers.Count; i++)
        {
            CHelper chelper = m_CHelpers.Get(i);
            GameObject helper = Convert(chelper);
            m_AllBones[chelper.NodeId] = helper;
        }

        // Add the root bone.
        m_AllBones.Add(m_AllBones.Keys.Max() + 1, m_SkeletonRoot);

    }

    public void InitializeBonesRelation()
    {
        // Set the bones' parents.
        for (int i = 0; i < m_CBones.Count; i++)
        {
            CBone cbone = m_CBones.Get(i);

            GameObject bone = m_AllBones[cbone.NodeId];
            if (m_AllBones.ContainsKey(cbone.Parent.NodeId))
            {
                GameObject parent = m_AllBones[cbone.Parent.NodeId];
                bone.transform.SetParent(parent.transform);
            }
            else
            {
                bone.transform.SetParent(m_SkeletonRoot.transform);
            }
        }

        // Set the helpers' parents.
        for (int i = 0; i < m_CHelpers.Count; i++)
        {
            CHelper chelper = m_CHelpers.Get(i);

            GameObject helper = m_AllBones[chelper.NodeId];
            if (m_AllBones.ContainsKey(chelper.Parent.NodeId))
            {
                GameObject parent = m_AllBones[chelper.Parent.NodeId];
                helper.transform.SetParent(parent.transform);
            }
            else
            {
                helper.transform.SetParent(m_SkeletonRoot.transform);
            }
        }
    }

    private void InitializeEvents()
    {
        CObjectContainer<CEvent> cevents = m_CModel.Events;
        for (int i = 0; i < cevents.Count; i++)
        {
            CEvent cevent = cevents.Get(i);
            CreateGameObject(cevent.Name, cevent.PivotPoint, cevent.Parent.NodeId);
        }
    }

    private void InitializeParticleEmitters()
    {
        CObjectContainer<CParticleEmitter> cemitters = m_CModel.ParticleEmitters;
        for (int i = 0; i < cemitters.Count; i++)
        {
            CParticleEmitter cemitter = cemitters.Get(i);
            CreateGameObject(cemitter.Name, cemitter.PivotPoint, cemitter.Parent.NodeId);
        }

        CObjectContainer<CParticleEmitter2> cparticles2 = m_CModel.ParticleEmitters2;
        for (int i = 0; i < cparticles2.Count; i++)
        {
            CParticleEmitter2 cparticle2 = cparticles2.Get(i);
            CreateGameObject(cparticle2.Name, cparticle2.PivotPoint, cparticle2.Parent.NodeId);
        }
    }

    private void InitializeCollisionShapes()
    {
        CObjectContainer<CCollisionShape> cshapes = m_CModel.CollisionShapes;
        for (int i = 0; i < cshapes.Count; i++)
        {
            CCollisionShape cshape = cshapes.Get(i);
            GameObject gameObject = CreateGameObject(cshape.Name, cshape.PivotPoint, cshape.Parent.NodeId);

            switch (cshape.Type)
            {
                case ECollisionShapeType.Box:
                    {
                        BoxCollider collider = gameObject.AddComponent<BoxCollider>();
                        collider.size = cshape.Vertex2.ToVector3() - cshape.Vertex1.ToVector3();
                        break;
                    }
                case ECollisionShapeType.Sphere:
                    {
                        SphereCollider collider = gameObject.AddComponent<SphereCollider>();
                        collider.radius = cshape.Radius;
                        break;
                    }
            }
        }
    }


    private CModel ReadModelFromFile()
    {
        CModel cmodel = null;
        try
        {
            cmodel = new CModel();
            using (FileStream stream = new FileStream(m_Path, FileMode.Open, FileAccess.Read))
            {
                string extension = Path.GetExtension(m_Path);
                if (extension.Equals(".mdx"))
                {
                    CMdx cmdx = new CMdx();
                    cmdx.Load(m_Path, stream, cmodel);
                }
                else if (extension.Equals(".mdl"))
                {
                    CMdl cmdl = new CMdl();
                    cmdl.Load(m_Path, stream, cmodel);
                }
                else
                {
                    throw new IOException("Invalid file extension.");
                }
            }
        }
        catch (Exception e)
        {
            Debug.LogError(e.Message);
        }

        m_CBones = cmodel.Bones;
        m_CHelpers = cmodel.Helpers;

        return cmodel;
    }

    private GameObject Convert(INode node)
    {
        GameObject bone = new GameObject(node.Name);

        // Pivot points are the positions of each object.
        CVector3 cpivot = node.PivotPoint;

        // Set the bone position.
        // MDX/MDL up axis is Z.
        // Unity up axis is Y.
        bone.transform.position = new Vector3(cpivot.X, cpivot.Z, cpivot.Y);

        return bone;
    }

    private GameObject CreateGameObject(string name, CVector3 pivot, int parentId)
    {
        GameObject gameObject = new GameObject(name);

        // Set the position.
        // MDX/MDL up axis is Z.
        // Unity up axis is Y.
        gameObject.transform.position = new Vector3(pivot.X, pivot.Z, pivot.Y);

        // Set the parent.
        if (m_AllBones.ContainsKey(parentId))
        {
            GameObject parent = m_AllBones[parentId];
            gameObject.transform.SetParent(parent.transform);
        }
        else
        {
            gameObject.transform.SetParent(m_SkeletonRoot.transform);
        }

        return gameObject;
    }

    private string GetPath(GameObject bone)
    {
        if (!bone)
        {
            return "";
        }

        string path = bone.name;
        while (bone.transform.parent != m_SkeletonRoot.transform && bone.transform.parent != null)
        {
            bone = bone.transform.parent.gameObject;
            path = bone.name + "/" + path;
        }

        path = m_SkeletonRoot.name + "/" + path;

        return path;
    }

}