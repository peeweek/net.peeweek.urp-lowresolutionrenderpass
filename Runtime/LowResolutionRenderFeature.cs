using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Experimental.Rendering;
using System.Collections.Generic;

namespace LowResolution
{
    /// <summary>
    /// LowResolutionRenderFeature for URP : forked and tweaked from 
    /// </summary>
    public class LowResolutionRenderFeature : ScriptableRendererFeature
    {
        public static bool Enabled
        {
            get { return PlayerPrefs.GetInt("LowResolutionRenderFeature.Enabled", 1) == 1; }
            set { PlayerPrefs.SetInt("LowResolutionRenderFeature.Enabled", value ? 1 : 0); }
        }

        const string RENDER_TARGET_NAME = "_LowResolutionTransparent";
        public enum DOWN_SAMPLING
        {
            Half = 2,
            Quarter = 4
        }

        [System.Serializable]
        public class Settings
        {
            public RenderPassEvent renderPassEvent = RenderPassEvent.BeforeRenderingTransparents;
            public DOWN_SAMPLING downsampling = DOWN_SAMPLING.Half;
            public LayerMask layerMask = 0; // layer for VFX
            public bool ApplyToOpaque = false;
            public bool BlurComposite = false;
        }

        [SerializeField] Settings settings = new Settings();
        LowResolutionRenderPass lowResolutionRenderPass;
        RTHandle colorRTHandle, depthRTHandle;

        class LowResolutionRenderPass : ScriptableRenderPass
        {
            static readonly List<ShaderTagId> SHADER_TAG_ID = new List<ShaderTagId>
            {
                new ShaderTagId("SRPDefaultUnlit"),  // For Unlit
                new ShaderTagId("UniversalForward"), // For Lit Opaque
                new ShaderTagId("UniversalForwardOnly"), // For Lit Opaque
            };

            RTHandle colorRT, depthRT;
            Material copyDepth, blitMaterial;
            FilteringSettings filteringSettings;
            Settings settings;

            public LowResolutionRenderPass(Settings settings)
            {
                this.profilingSampler = new ProfilingSampler(nameof(LowResolutionRenderPass));
                this.renderPassEvent = settings.renderPassEvent;

                this.filteringSettings = new FilteringSettings(GetRenderQueueRange(settings.ApplyToOpaque), settings.layerMask);
                this.settings = settings;
                this.ResetTarget();
            }

            static RenderQueueRange GetRenderQueueRange(bool opaqueAsWell)
            {
                RenderQueueRange range = RenderQueueRange.transparent;
                if (opaqueAsWell)
                    range = RenderQueueRange.all;
                return range;
            }

            // Called at SetupRenderPasses
            public void Setup(RTHandle colorRT, RTHandle depthRT)
            {
                this.colorRT = colorRT;
                this.depthRT = depthRT;
            }

            readonly int _ScaledScreenParams = Shader.PropertyToID("_ScaledScreenParams");

            [System.Obsolete("TODO", false)]
            public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
            {
                // Lazy creation : as in Constructor Shader.Find() can return null;
                if (this.copyDepth == null) this.copyDepth = new Material(Shader.Find("LowResolution/CopyDownsampleDepth"));
                if (this.blitMaterial == null) this.blitMaterial = new Material(Shader.Find("LowResolution/CompositeLowResolution"));

                if (this.settings.BlurComposite)
                    this.blitMaterial.EnableKeyword("BLUR_OUTPUT");
                else
                    this.blitMaterial.DisableKeyword("BLUR_OUTPUT");

                this.filteringSettings.renderQueueRange = GetRenderQueueRange(this.settings.ApplyToOpaque);

                var isSupportedCamera = (renderingData.cameraData.cameraType == CameraType.Game && renderingData.cameraData.camera == Camera.main)
                                        || renderingData.cameraData.cameraType == CameraType.SceneView;
                                    
                                    
                var cmd = CommandBufferPool.Get();
                using (new ProfilingScope(cmd, this.profilingSampler))
                {
                    if (isSupportedCamera && Enabled)
                    {
                        cmd.SetRenderTarget(this.colorRT, this.depthRT);
                        cmd.ClearRenderTarget(true, true, Color.clear);

                        var targetDepthRTHandle = renderingData.cameraData.renderer.cameraDepthTargetHandle;
                        var scaleBias = new Vector4(1f, 1f, 0f, 0f);
                        Blitter.BlitTexture(cmd, targetDepthRTHandle, scaleBias, this.copyDepth, 0);
                    }
                    else
                    {
                        // other cameras do not need to use LowResolution(SceneView, Preview, etc...)
                        cmd.SetRenderTarget(renderingData.cameraData.renderer.cameraColorTargetHandle,
                                            renderingData.cameraData.renderer.cameraDepthTargetHandle);
                    }

                    // Rendering by LayerMask(e.g. any VFX)
                    var drawSettings =
                        CreateDrawingSettings(SHADER_TAG_ID, ref renderingData, SortingCriteria.CommonTransparent);
                    drawSettings.perObjectData = PerObjectData.None;

                    var param = new RendererListParams(renderingData.cullResults, drawSettings, this.filteringSettings);
                    var rl = context.CreateRendererList(ref param);

                    ///////////////////////////////////////////////////////////////////////////
                    // STAGE 1 : Draw renderers inside the low resolution render buffer
                    ///////////////////////////////////////////////////////////////////////////
                    if (isSupportedCamera && Enabled)
                    {
                        var cameraData = renderingData.cameraData;
                        int currentScaledWidth = Mathf.Max(1, (int)(cameraData.camera.pixelWidth * cameraData.renderScale));
                        int currentScaledHeight = Mathf.Max(1, (int)(cameraData.camera.pixelHeight * cameraData.renderScale));

                        // Handle ScaledScreenParams
                        Vector4 scaledScreenParams = Shader.GetGlobalVector(_ScaledScreenParams);
                        scaledScreenParams.x = currentScaledWidth / (int)settings.downsampling;
                        scaledScreenParams.y = currentScaledHeight / (int)settings.downsampling;
                        cmd.SetGlobalVector(_ScaledScreenParams, scaledScreenParams);
                        cmd.DrawRendererList(rl);

                        // Restore Unscaled Screen State
                        scaledScreenParams.x = currentScaledWidth;
                        scaledScreenParams.y = currentScaledHeight;
                        cmd.SetGlobalVector(_ScaledScreenParams, scaledScreenParams);
                    }
                    else
                    {
                        cmd.DrawRendererList(rl);
                    }

                    //////////////////////////////////////////////////////////////////////////////
                    // STAGE 2 : Composite lowres transparent buffer to the backbuffer.
                    //////////////////////////////////////////////////////////////////////////////
                    if (isSupportedCamera && Enabled)
                    {
                        var dstColorRT = renderingData.cameraData.renderer.cameraColorTargetHandle;
                        var viewportScale = Vector2.one;
                        cmd.SetRenderTarget(dstColorRT);
                        Blitter.BlitTexture(cmd, this.colorRT, viewportScale, this.blitMaterial, 0);

                        // Restore render targets.
                        cmd.SetRenderTarget(renderingData.cameraData.renderer.cameraColorTargetHandle,
                                            renderingData.cameraData.renderer.cameraDepthTargetHandle);
                    }
                }

                context.ExecuteCommandBuffer(cmd);
                CommandBufferPool.Release(cmd);
            }

            public override void OnCameraCleanup(CommandBuffer cmd)
            {
                if (cmd == null)
                    throw new System.ArgumentNullException("LowResolutionRenderFeature.OnCameraCleanup() : No Commandbuffer provided, or null.");
                this.colorRT = null;
                this.depthRT = null;
            }

            public void Dispose()
            {
                CoreUtils.Destroy(this.copyDepth);
                CoreUtils.Destroy(this.blitMaterial);
                this.copyDepth = this.blitMaterial = null;
            }
        }

        public override void Create()
        {
            if (this.lowResolutionRenderPass == null)
                this.lowResolutionRenderPass = new LowResolutionRenderPass(this.settings);
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            renderer.EnqueuePass(this.lowResolutionRenderPass);
        }

        public override void SetupRenderPasses(ScriptableRenderer renderer, in RenderingData renderingData)
        {
            var desc = renderingData.cameraData.cameraTargetDescriptor;
            // Create RTHandle with RenderingUtils.ReAllocateIfNeeded
            var downSampling = (int)this.settings.downsampling;
            desc.width = desc.width / downSampling;
            desc.height = desc.height / downSampling;
            desc.graphicsFormat = GraphicsFormat.R16G16B16A16_SFloat;

            var depthDesc = desc;
            depthDesc.msaaSamples = 1;// Depth-Only pass don't use MSAA
            depthDesc.graphicsFormat = GraphicsFormat.None; // DepthBufferとしてColorBufferにBindさせるにはR32ではダメ
            RenderingUtils.ReAllocateHandleIfNeeded(ref this.depthRTHandle,
                                                    depthDesc,
                                                    FilterMode.Point,
                                                    TextureWrapMode.Clamp,
                                                    1, 0, RENDER_TARGET_NAME);

            // must set 0 to use as DepthBuffer
            // automatically set stencilFormat when set depthBufferBits
            desc.depthBufferBits = 0;
            RenderingUtils.ReAllocateHandleIfNeeded(ref this.colorRTHandle,
                                                    desc,
                                                    FilterMode.Bilinear, // BilinearだとDepthとのEdgeは綺麗だが全体的にボケが強い
                                                    TextureWrapMode.Clamp,
                                                    1, 0, RENDER_TARGET_NAME);

            this.lowResolutionRenderPass.Setup(this.colorRTHandle, this.depthRTHandle);
        }

        protected override void Dispose(bool disposing)
        {
            this.lowResolutionRenderPass?.Dispose();
            this.lowResolutionRenderPass = null;

            this.colorRTHandle?.Release();
            this.depthRTHandle?.Release();
            this.colorRTHandle = this.depthRTHandle = null;
        }
    }
}
