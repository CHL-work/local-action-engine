using System;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Brainwash
{
    // Automation captures the camera directly. A hidden CI Player has no
    // presented swapchain frame for ScreenCapture.CaptureScreenshot to read.
    // These images verify scene rendering; they deliberately exclude IMGUI HUD.
    internal static class DemoCapture
    {
        public static void Save(Camera camera, string path)
        {
            if (camera == null) throw new InvalidOperationException("Demo capture needs the world camera.");
            const int width = 960, height = 640;
            var request = new UniversalRenderPipeline.SingleCameraRequest();
            if (!RenderPipeline.SupportsRenderRequest(camera, request))
                throw new InvalidOperationException("The render pipeline does not support demo capture requests.");
            var target = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var previousActive = RenderTexture.active;
            var previousRect = camera.rect;
            var previousAspect = camera.aspect;
            Texture2D image = null;
            try
            {
                camera.rect = new Rect(0, 0, 1, 1);
                camera.aspect = (float)width / height;
                request.destination = target;
                RenderPipeline.SubmitRenderRequest(camera, request);
                RenderTexture.active = target;
                image = new Texture2D(width, height, TextureFormat.RGB24, false);
                image.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                image.Apply(false, false);
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
                File.WriteAllBytes(path, image.EncodeToPNG());
            }
            finally
            {
                camera.rect = previousRect;
                camera.aspect = previousAspect;
                RenderTexture.active = previousActive;
                if (image != null) UnityEngine.Object.Destroy(image);
                RenderTexture.ReleaseTemporary(target);
            }
        }
    }
}
