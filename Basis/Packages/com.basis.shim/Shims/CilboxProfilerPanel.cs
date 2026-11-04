using System.Collections.Generic;
using Unity.Scripting.LifecycleManagement;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Basis.BasisUI;
using Basis.BasisUI.Styling;
using Basis.Scripts.Drivers;

namespace Cilbox
{
	[AutoStaticsCleanup]
	public sealed partial class CilboxProfilerPanel : MonoBehaviour
	{
		internal const string ShaderResource = "BasisCilboxProfilerGraph";
		const float GraphHeight = 120f, FallbackWidth = 600f;
		const int TargetWidth = CilboxProfiler.GraphFrames * 2, TargetHeight = 120;
		[NoAutoStaticsCleanup] static readonly float[] steps = { 0.05f, 0.1f, 0.2f, 0.5f, 1f, 2f, 5f, 10f, 20f, 50f, 100f, 200f, 500f, 1000f, 2000f, 5000f };
		[NoAutoStaticsCleanup] static readonly int idColors = Shader.PropertyToID("_Colors"), idBackground = Shader.PropertyToID("_Background"), idGrid = Shader.PropertyToID("_Grid"), idHead = Shader.PropertyToID("_Head"), idScale = Shader.PropertyToID("_Scale");
		static Shader shader;
		static bool shaderMissing;
		PanelElementDescriptor graphGroup;
		RawImage graph;
		TextMeshProUGUI table;
		LayoutElement tableLayout;
		Material material;
		RenderTexture target;
		Texture2D history;
		ushort[] halves;
		float scale = steps[0], shownWidth = -1f, measuredSize = -1f;
		int graphVersion = int.MinValue, tableVersion = int.MinValue, sortVersion = int.MinValue, captionTenths = -1;
		float captionScale = -1f;
		readonly Dictionary<string, float> widths = new Dictionary<string, float>();
		readonly Vector4[] colors = new Vector4[CilboxProfiler.GraphRows];
		internal static CilboxProfilerPanel Create(PanelElementDescriptor graphGroup, PanelElementDescriptor tableGroup)
		{
			RectTransform graphParent = graphGroup.ContentParent;
			GameObject image = new GameObject("Cilbox Profiler Graph", typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage));
			image.layer = graphParent.gameObject.layer;
			image.transform.SetParent(graphParent, false);
			RawImage raw = image.GetComponent<RawImage>();
			raw.raycastTarget = false;
			raw.color = Color.white;
			raw.material = BasisWaveformGraphic.ResolveOverlayMaterial();
			LayoutElement graphLayout = image.AddComponent<LayoutElement>();
			graphLayout.minHeight = graphLayout.preferredHeight = GraphHeight;
			graphLayout.flexibleWidth = 1f;
			RectTransform tableParent = tableGroup.ContentParent;
			GameObject label = new GameObject("Cilbox Profiler Table", typeof(RectTransform));
			label.layer = tableParent.gameObject.layer;
			label.transform.SetParent(tableParent, false);
			TextMeshProUGUI text = label.AddComponent<TextMeshProUGUI>();
			label.AddComponent<UiStyleLabel>().SetStyle("Element Subtitle");
			text.textWrappingMode = TextWrappingModes.NoWrap;
			text.overflowMode = TextOverflowModes.Overflow;
			text.alignment = TextAlignmentOptions.TopLeft;
			text.richText = true;
			text.raycastTarget = false;
			LayoutElement layout = label.AddComponent<LayoutElement>();
			layout.minWidth = 0f;
			layout.preferredWidth = 0f;
			layout.flexibleWidth = 1f;
			CilboxProfilerPanel panel = tableGroup.gameObject.AddComponent<CilboxProfilerPanel>();
			panel.graphGroup = graphGroup;
			panel.graph = raw;
			panel.table = text;
			panel.tableLayout = layout;
			panel.Refresh();
			return panel;
		}
		internal static float Rescale(float current, float peak)
		{
			float wanted = steps[steps.Length - 1];
			for (int i = 0; i < steps.Length; i++)
			{
				if (steps[i] < peak * 1.1f) continue;
				wanted = steps[i];
				break;
			}
			return wanted > current || wanted * 4f <= current ? wanted : current;
		}
		void OnEnable()
		{
			BasisFrameClock.AddRequest();
			BasisFrameClock.OnTick += OnTick;
			Refresh();
		}
		void OnDisable()
		{
			BasisFrameClock.OnTick -= OnTick;
			BasisFrameClock.RemoveRequest();
		}
		void OnDestroy()
		{
			if (graph != null) graph.texture = null;
			if (target != null)
			{
				target.Release();
				Destroy(target);
				target = null;
			}
			if (material != null)
			{
				Destroy(material);
				material = null;
			}
			if (history != null)
			{
				Destroy(history);
				history = null;
			}
		}
		void Refresh()
		{
			graphVersion = tableVersion = sortVersion = int.MinValue;
			OnTick();
		}
		void OnTick()
		{
			bool active = CilboxProfiler.Active;
			if (graphGroup != null && graphGroup.gameObject.activeSelf != active) graphGroup.gameObject.SetActive(active);
			if (active && graph != null && graph.isActiveAndEnabled && CilboxProfiler.GraphVersion != graphVersion) DrawGraph();
			if (table != null) DrawTable();
		}
		void DrawGraph()
		{
			graphVersion = CilboxProfiler.GraphVersion;
			if (!EnsureGraph()) return;
			float[] data = CilboxProfiler.Graph;
			if (halves == null) history.SetPixelData(data, 0);
			else
			{
				for (int i = 0; i < data.Length; i++) halves[i] = Mathf.FloatToHalf(data[i]);
				history.SetPixelData(halves, 0);
			}
			history.Apply(false, false);
			float peak = 0f;
			for (int f = 0; f < CilboxProfiler.GraphFrames; f++)
			{
				float sum = 0f;
				for (int row = 0; row < CilboxProfiler.GraphRows; row++) sum += data[row * CilboxProfiler.GraphFrames + f];
				if (sum > peak) peak = sum;
			}
			scale = Rescale(scale, peak);
			material.SetFloat(idHead, CilboxProfiler.GraphHead);
			material.SetFloat(idScale, scale);
			Graphics.Blit(history, target, material);
			int tenths = Mathf.RoundToInt(CilboxProfiler.GraphFrames * 10f / Mathf.Max(1f, BasisFrameClock.SmoothedFramesPerSecond));
			if (tenths == captionTenths && scale == captionScale) return;
			captionTenths = tenths;
			captionScale = scale;
			graphGroup.SetDescription(CilboxProfilerView.Caption(scale, tenths / 10f));
		}
		bool EnsureGraph()
		{
			if (material != null && target != null && history != null) return true;
			if (shaderMissing) return false;
			if (shader == null) shader = Resources.Load<Shader>(ShaderResource);
			TextureFormat format = SystemInfo.SupportsTextureFormat(TextureFormat.RFloat) ? TextureFormat.RFloat : TextureFormat.RHalf;
			if (shader == null || !SystemInfo.SupportsTextureFormat(format))
			{
				shaderMissing = true;
				BasisDebug.LogWarning("[Cilbox] The script profiler graph cannot render on this device; the table still works.");
				return false;
			}
			material = new Material(shader) { name = "Cilbox Profiler Graph", hideFlags = HideFlags.HideAndDontSave };
			Color[] palette = BasisPerformanceBarView.CpuPalette;
			bool linear = QualitySettings.activeColorSpace == ColorSpace.Linear;
			for (int i = 0; i < colors.Length; i++)
			{
				Color color = palette[Mathf.Min(i, palette.Length - 1)];
				colors[i] = linear ? color.linear : color;
			}
			material.SetVectorArray(idColors, colors);
			material.SetColor(idBackground, new Color(0f, 0f, 0f, 0.25f));
			material.SetColor(idGrid, new Color(1f, 1f, 1f, 0.12f));
			target = new RenderTexture(TargetWidth, TargetHeight, 0, RenderTextureFormat.ARGB32)
			{
				name = material.name,
				filterMode = FilterMode.Bilinear,
				wrapMode = TextureWrapMode.Clamp,
				useMipMap = false,
				autoGenerateMips = false,
				hideFlags = HideFlags.HideAndDontSave,
			};
			target.Create();
			history = new Texture2D(CilboxProfiler.GraphFrames, CilboxProfiler.GraphRows, format, false, true)
			{
				name = "Cilbox Profiler History",
				filterMode = FilterMode.Point,
				wrapMode = TextureWrapMode.Clamp,
				hideFlags = HideFlags.HideAndDontSave,
			};
			if (format == TextureFormat.RHalf) halves = new ushort[CilboxProfiler.GraphFrames * CilboxProfiler.GraphRows];
			graph.texture = target;
			return true;
		}
		void DrawTable()
		{
			if (!Mathf.Approximately(table.fontSize, measuredSize))
			{
				widths.Clear();
				measuredSize = table.fontSize;
				tableVersion = int.MinValue;
			}
			float width = table.rectTransform.rect.width;
			if (tableVersion == CilboxProfiler.Version && sortVersion == CilboxProfilerView.SortVersion && Mathf.Abs(width - shownWidth) < 1f) return;
			tableVersion = CilboxProfiler.Version;
			sortVersion = CilboxProfilerView.SortVersion;
			shownWidth = width;
			float available = width > 1f ? width : FallbackWidth;
			table.textWrappingMode = TextWrappingModes.NoWrap;
			string body = CilboxProfilerView.RenderTable(CilboxProfilerView.Order, available, Measure);
			if (body.IndexOf("<pos=", System.StringComparison.Ordinal) < 0) table.textWrappingMode = TextWrappingModes.Normal;
			table.SetText(body);
			float height = table.GetPreferredValues(body, available, 0f).y;
			if (Mathf.Abs(tableLayout.preferredHeight - height) < 0.5f) return;
			tableLayout.minHeight = tableLayout.preferredHeight = height;
			LayoutRebuilder.MarkLayoutForRebuild(table.rectTransform);
		}
		float Measure(string text)
		{
			if (widths.TryGetValue(text, out float width)) return width;
			if (widths.Count >= 4096) widths.Clear();
			width = table.GetPreferredValues(text).x;
			widths[text] = width;
			return width;
		}
	}
}
