using System;
using System.Collections.Generic;
using System.Globalization;
using Basis.BasisUI;
using Basis.Scripts.Settings;

namespace Basis.Shims
{
	/// <summary>
	/// What the player's graphics settings are set to, so a world can scale itself to the machine
	/// it is running on instead of shipping one budget for a phone and a 4090.
	///
	/// <code>
	/// // cheap tiers lose the donut and take a flat sky
	/// bool cheap = BasisGraphicsSettingsShim.QualityTier &lt;= BasisGraphicsSettingsShim.TierLow;
	/// backgroundDonut.SetActive( !cheap );
	/// RenderSettings.skybox = cheap ? flatSky : cloudSky;
	/// </code>
	///
	/// Pair it with <see cref="BasisGraphicsSettingsEventShim"/> to be told when the player changes
	/// any of this, rather than polling every frame. Nothing here is a write handle — a world can
	/// read the player's choices and adapt, it cannot change them.
	///
	/// <para>Tiers are the useful half. <see cref="QualityTier"/> and <see cref="ShadowTier"/> are
	/// ordinals over <see cref="Tiers"/>, cheapest first, so a script compares numbers instead of
	/// spelling "Very Low" correctly. The raw strings are still available where a world wants to
	/// show them.</para>
	///
	/// <para><see cref="Get"/> reaches the rest of the graphics settings by key, over the fixed
	/// allowlist in <see cref="ReadableKeys"/>. That list is deliberately short and deliberately
	/// graphics-only: reading a setting is also a fingerprinting surface, so the boundary sits at
	/// "what a world needs to draw itself cheaper", not at the whole settings file. Anything not on
	/// the list answers with the fallback and never throws.</para>
	///
	/// <para>Values are reported exactly as the client stores them — tier names in their display
	/// casing ("Very Low", "Ultra"), bools as "true"/"false", numbers in invariant culture. Compare
	/// them case-insensitively, or use the typed helpers, which already do.</para>
	///
	/// <para>Performance Mode writes through these same settings, so a level the client picked for
	/// itself — a crowded instance dropping to Aggressive — shows up here as the lower tier it
	/// actually applied. <see cref="PerformanceTier"/> reports that separately for a world that
	/// wants to tell "the player chose Low" from "the client forced Low".</para>
	/// </summary>
	public static class BasisGraphicsSettingsShim
	{
		/// <summary>Longest key a query may name. Anything longer is refused without a lookup.</summary>
		public const int MaxKeyLength = 64;

		public const int TierVeryLow = 0;
		public const int TierLow = 1;
		public const int TierMedium = 2;
		public const int TierHigh = 3;
		public const int TierUltra = 4;

		public const int PerformanceOff = 0;
		public const int PerformanceLight = 1;
		public const int PerformanceBalanced = 2;
		public const int PerformanceAggressive = 3;

		public const string KeyQualityLevel = "qualitylevel";
		public const string KeyShadowQuality = "shadowquality";
		public const string KeyAntialiasing = "antialiasing";
		public const string KeyHdrSupport = "hdrsupport";
		public const string KeyRenderResolution = "render resolution";

		private static readonly string[] tierNames = { "Very Low", "Low", "Medium", "High", "Ultra" };

		// Every key a sandboxed script may read, and the binding that answers for it. Adding a row
		// is the only way to widen the surface; there is no prefix rule and no passthrough.
		private static readonly KeyValuePair<string, Func<string>>[] readable =
		{
			new KeyValuePair<string, Func<string>>( KeyQualityLevel, () => BasisSettingsDefaults.QualityLevel.RawValue ),
			new KeyValuePair<string, Func<string>>( KeyShadowQuality, () => BasisSettingsDefaults.ShadowQuality.RawValue ),
			new KeyValuePair<string, Func<string>>( KeyAntialiasing, () => BasisSettingsDefaults.Antialiasing.RawValue ),
			new KeyValuePair<string, Func<string>>( KeyHdrSupport, () => BasisSettingsDefaults.HDRSupport.RawValue ),
			new KeyValuePair<string, Func<string>>( KeyRenderResolution, () => Number( BasisSettingsDefaults.RenderResolution.RawValue ) ),
			new KeyValuePair<string, Func<string>>( "dynamicresolutionenabled", () => Flag( BasisSettingsDefaults.DynamicResolutionEnabled.RawValue ) ),
			new KeyValuePair<string, Func<string>>( "globalmeshlod", () => Number( BasisSettingsDefaults.GlobalMeshLOD.RawValue ) ),
			new KeyValuePair<string, Func<string>>( "avatarmeshlod", () => Number( BasisSettingsDefaults.AvatarMeshLOD.RawValue ) ),
			new KeyValuePair<string, Func<string>>( "useglobalillumination", () => Flag( BasisSettingsDefaults.UseGlobalIllumination.RawValue ) ),
			new KeyValuePair<string, Func<string>>( "globalilluminationquality", () => BasisSettingsDefaults.GlobalIlluminationQuality.RawValue ),
			new KeyValuePair<string, Func<string>>( "globalilluminationresolution", () => BasisSettingsDefaults.GlobalIlluminationResolution.RawValue ),
			new KeyValuePair<string, Func<string>>( "useraytracedambientocclusion", () => Flag( BasisSettingsDefaults.UseRayTracedAmbientOcclusion.RawValue ) ),
			new KeyValuePair<string, Func<string>>( "raytracedambientocclusionquality", () => BasisSettingsDefaults.RayTracedAmbientOcclusionQuality.RawValue ),
			new KeyValuePair<string, Func<string>>( "motionblurquality", () => BasisSettingsDefaults.MotionBlurQuality.RawValue ),
			new KeyValuePair<string, Func<string>>( "usevolumetricfogoverride", () => Flag( BasisSettingsDefaults.UseVolumetricFogOverride.RawValue ) ),
			new KeyValuePair<string, Func<string>>( "usebloomoverride", () => Flag( BasisSettingsDefaults.UseBloomOverride.RawValue ) ),
		};

		private static readonly Dictionary<string, Func<string>> lookup = BuildLookup();
		private static readonly Dictionary<string, string> overrides = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
		private static bool subscribed;

		/// <summary>
		/// Raised after any readable graphics setting changes. Coalescing and the hop into
		/// interpreted code both live in <see cref="BasisGraphicsSettingsEventShim"/>; this is the
		/// native-side signal it listens to.
		/// </summary>
		internal static event Action OnReadableSettingChanged;

		private static Dictionary<string, Func<string>> BuildLookup()
		{
			Dictionary<string, Func<string>> map = new Dictionary<string, Func<string>>( readable.Length, StringComparer.OrdinalIgnoreCase );
			for( int i = 0; i < readable.Length; i++ )
			{
				map[readable[i].Key] = readable[i].Value;
			}
			return map;
		}

		private static string Number( float value ) => value.ToString( CultureInfo.InvariantCulture );

		private static string Flag( bool value ) => value ? "true" : "false";

		/// <summary>
		/// Settings are written two ways — through a binding, which updates RawValue, and straight
		/// into the store by key, which does not. Watching the store and keeping the by-key writes
		/// as an override layer means a read is correct whichever path a change took, without
		/// re-reading the whole file.
		/// </summary>
		private static void EnsureSubscribed()
		{
			if( subscribed ) return;
			subscribed = true;
			BasisSettingsSystem.OnSettingChanged += OnSettingChanged;
		}

		/// <summary>
		/// Starts watching the settings store if nothing has read from this shim yet, so a script
		/// that only listens through <see cref="BasisGraphicsSettingsEventShim"/> still gets told.
		/// </summary>
		internal static void EnsureWatching() => EnsureSubscribed();

		private static void OnSettingChanged( string key, string value )
		{
			if( string.IsNullOrEmpty( key ) || !lookup.ContainsKey( key ) ) return;
			overrides[key] = value;
			OnReadableSettingChanged?.Invoke();
		}

		/// <summary>Tier names a quality setting can take, cheapest first. A fresh copy each call.</summary>
		public static string[] Tiers => (string[])tierNames.Clone();

		/// <summary>Every key <see cref="Get"/> will answer for. A fresh copy each call.</summary>
		public static string[] ReadableKeys
		{
			get
			{
				string[] keys = new string[readable.Length];
				for( int i = 0; i < readable.Length; i++ ) keys[i] = readable[i].Key;
				return keys;
			}
		}

		/// <summary>Whether <paramref name="key"/> is one this shim will answer for.</summary>
		public static bool IsReadable( string key )
		{
			if( string.IsNullOrEmpty( key ) || key.Length > MaxKeyLength ) return false;
			return lookup.ContainsKey( key );
		}

		/// <summary>
		/// The current value of a graphics setting, exactly as the client stores it. Empty string
		/// for a key that is not on <see cref="ReadableKeys"/>.
		/// </summary>
		public static string Get( string key )
		{
			if( string.IsNullOrEmpty( key ) || key.Length > MaxKeyLength ) return string.Empty;

			EnsureSubscribed();

			if( overrides.TryGetValue( key, out string stored ) ) return stored ?? string.Empty;
			if( !lookup.TryGetValue( key, out Func<string> read ) ) return string.Empty;

			string value = read();
			return value ?? string.Empty;
		}

		/// <summary>Numeric form of <see cref="Get"/>. <paramref name="fallback"/> when unreadable or not a number.</summary>
		public static float GetNumber( string key, float fallback )
		{
			string value = Get( key );
			if( value.Length == 0 ) return fallback;
			return float.TryParse( value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out float parsed )
				? parsed
				: fallback;
		}

		/// <summary>Boolean form of <see cref="Get"/>. <paramref name="fallback"/> when unreadable or not a bool.</summary>
		public static bool GetFlag( string key, bool fallback )
		{
			string value = Get( key );
			if( string.Equals( value, "true", StringComparison.OrdinalIgnoreCase ) ) return true;
			if( string.Equals( value, "false", StringComparison.OrdinalIgnoreCase ) ) return false;
			return fallback;
		}

		/// <summary>
		/// Index of a tier-valued setting into <see cref="Tiers"/>, cheapest first, or -1 when the
		/// key is unreadable or does not hold a tier name.
		/// </summary>
		public static int GetTier( string key ) => TierIndex( Get( key ) );

		/// <summary>Index of <paramref name="tierName"/> into <see cref="Tiers"/>, or -1.</summary>
		public static int TierIndex( string tierName )
		{
			if( string.IsNullOrEmpty( tierName ) ) return -1;
			for( int i = 0; i < tierNames.Length; i++ )
			{
				if( string.Equals( tierNames[i], tierName, StringComparison.OrdinalIgnoreCase ) ) return i;
			}
			return -1;
		}

		/// <summary>Overall graphics quality, as the player set it — "Very Low" through "Ultra".</summary>
		public static string QualityLevel => Get( KeyQualityLevel );

		/// <summary>
		/// <see cref="QualityLevel"/> as an ordinal, cheapest first: 0 Very Low, 4 Ultra, -1 unknown.
		/// This is the one most worlds want.
		/// </summary>
		public static int QualityTier => GetTier( KeyQualityLevel );

		/// <summary>Shadow quality, as the player set it.</summary>
		public static string ShadowQuality => Get( KeyShadowQuality );

		/// <summary><see cref="ShadowQuality"/> as an ordinal, cheapest first, or -1.</summary>
		public static int ShadowTier => GetTier( KeyShadowQuality );

		/// <summary>Antialiasing mode, e.g. "Off" or "MSAA 2X".</summary>
		public static string Antialiasing => Get( KeyAntialiasing );

		/// <summary>Render scale the client draws at. 1 is native.</summary>
		public static float RenderResolution => GetNumber( KeyRenderResolution, 1f );

		/// <summary>Whether real-time global illumination is on.</summary>
		public static bool GlobalIlluminationEnabled => GetFlag( "useglobalillumination", false );

		/// <summary>Whether ray-traced ambient occlusion is on.</summary>
		public static bool RayTracedAmbientOcclusionEnabled => GetFlag( "useraytracedambientocclusion", false );

		/// <summary>
		/// Performance Mode's active level as an ordinal — 0 off, 3 aggressive. Distinct from
		/// <see cref="QualityTier"/>: this says the client is holding the settings down on its own,
		/// where the tier says where they ended up.
		/// </summary>
		public static int PerformanceTier => (int)BasisPerformanceMode.ActiveLevel;

		/// <summary>Performance Mode's active level as its id — "off", "light", "balanced", "aggressive".</summary>
		public static string PerformanceLevel => BasisPerformanceMode.LevelToId( BasisPerformanceMode.ActiveLevel );
	}
}
