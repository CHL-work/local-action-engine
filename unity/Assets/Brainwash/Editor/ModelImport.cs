using UnityEditor;

namespace Brainwash.Editor
{
    // Import settings for the generated low-poly models (art/models/build_models.py):
    // plain bone transforms posed by CreatureAnimator, no clips, cameras or lights,
    // and no imported materials (ModelLibrary paints them from the palette).
    // Also sets how the synthesized sounds (art/audio/generate_sfx.py) load.
    sealed class ModelImport : AssetPostprocessor
    {
        const string Folder = "Assets/Brainwash/Resources/Models/";
        const string AudioFolder = "Assets/Brainwash/Resources/Audio/";

        // Long loops (ambience, drones) stay compressed in memory; short effects
        // decompress on load so they start without decoding work.
        void OnPreprocessAudio()
        {
            if (!assetPath.StartsWith(AudioFolder)) return;
            var importer = (AudioImporter)assetImporter;
            string name = System.IO.Path.GetFileNameWithoutExtension(assetPath);
            bool longLoop = name.StartsWith("amb_") || name == "chant_pad" || name.EndsWith("_loop");
            var settings = importer.defaultSampleSettings;
            settings.loadType = longLoop ? UnityEngine.AudioClipLoadType.CompressedInMemory : UnityEngine.AudioClipLoadType.DecompressOnLoad;
            importer.defaultSampleSettings = settings;
        }

        void OnPreprocessModel()
        {
            if (!assetPath.StartsWith(Folder)) return;
            var importer = (ModelImporter)assetImporter;
            importer.globalScale = 1;
            importer.useFileScale = true;
            importer.bakeAxisConversion = true;
            importer.importCameras = false;
            importer.importLights = false;
            importer.importVisibility = false;
            importer.importBlendShapes = false;
            importer.importAnimation = false;
            importer.animationType = ModelImporterAnimationType.Generic;
            importer.avatarSetup = ModelImporterAvatarSetup.NoAvatar;
            importer.optimizeGameObjects = false;
            importer.materialImportMode = ModelImporterMaterialImportMode.None;
            importer.meshCompression = ModelImporterMeshCompression.Off;
            importer.optimizeBones = false;
            importer.isReadable = false;
            importer.importNormals = ModelImporterNormals.Import;
            importer.importTangents = ModelImporterTangents.None;
            importer.preserveHierarchy = true;
            importer.sortHierarchyByName = false;
        }
    }
}
