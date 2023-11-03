using System.Collections.Generic;
using System.IO;
using UnityEditor.AssetImporters;
using UnityEditor;
using UnityEngine;
using System.Linq;

[ScriptedImporter(1, new[] { "mdx", "mdl" })]
public class MyMdxScriptedImporter : ScriptedImporter
{
    // General.
    public bool importAttachments = false;
    public bool importEvents = false;
    public bool importParticleEmitters = false;
    public bool importCollisionShapes = false;
    public List<int> excludeGeosets = new List<int>();
    public List<string> excludeByTexture = new List<string>() { };

    // Materials.
    public bool importMaterials = true;
    public bool addMaterialsToAsset = false;

    // Animations.
    public bool importAnimations = true;
    public bool addAnimationsToAsset = false;
    public bool importTangents = true;
    public float frameRate = 960;
    public List<string> excludeAnimations = new List<string>() { };

    public override void OnImportAsset(AssetImportContext context)
    {
        string directoryPath = Path.GetDirectoryName(context.assetPath).Replace('\\', '/');

        MdxImportSettings settings = new MdxImportSettings()
        {
            importAttachments = importAttachments,
            importEvents = importEvents,
            importParticleEmitters = importParticleEmitters,
            importCollisionShapes = importCollisionShapes,
            excludeGeosets = excludeGeosets,
            excludeByTexture = excludeByTexture,
            importMaterials = importMaterials,
            importAnimations = importAnimations,
            importTangents = importTangents,
            frameRate = frameRate,
            excludeAnimations = excludeAnimations
        };

        GameObject gameObject = new GameObject();
        MyMdxModel model = new MyMdxModel(context.assetPath);

        Mesh mesh = model.GetMesh(settings.excludeGeosets, settings.excludeByTexture, gameObject);

        SkinnedMeshRenderer renderer = gameObject.AddComponent<SkinnedMeshRenderer>();
        renderer.sharedMesh = mesh;
        // Set the bones to the skinned mesh renderer.
        renderer.bones = model.BonesTransforms.ToArray();
        renderer.rootBone = model.SkeletonRoot.transform;

        MeshFilter filter = gameObject.AddComponent<MeshFilter>();
        filter.mesh = mesh;

        model.SkeletonRoot.transform.SetParent(gameObject.transform);

        context.AddObjectToAsset("prefab", gameObject);
        context.SetMainObject(gameObject);
        context.AddObjectToAsset("mesh", mesh);


        if (importMaterials)
        {
            List<Material> materials = model.GetMaterials();
            renderer.materials = materials.ToArray();
            int count = materials.Count;
            Debug.Log("importMaterials:" + count);
            for (int i = 0; i < count; i++)
            {
                Material material = materials[i];
                if (addMaterialsToAsset)
                {
                    context.AddObjectToAsset(material.name, material);
                }
                else
                {
                    string directory = directoryPath + "/Materials/";
                    Directory.CreateDirectory(directory);

                    AssetDatabase.CreateAsset(material, directory + material.name + ".mat");
                    AssetDatabase.SaveAssets();
                }
            }
        }

        if (importAnimations)
        {
            List<AnimationClip> animationClips = model.GetAnimationClips(settings.frameRate, settings.importTangents, settings.excludeAnimations);

            foreach (AnimationClip clip in animationClips)
            {
                if (addAnimationsToAsset)
                {
                    context.AddObjectToAsset(clip.name, clip);
                }
                else
                {
                    string directory = directoryPath + "/Animations/";
                    Directory.CreateDirectory(directory);

                    AssetDatabase.CreateAsset(clip, directory + clip.name + ".anim");
                    AssetDatabase.SaveAssets();
                }
            }
        }
    }
}