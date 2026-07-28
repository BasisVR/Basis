using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Basis.BasisUI;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Networking.NetworkedAvatar;
using UnityEngine;

public static class BasisHandHeldCameraPhotoMetadata
{
    public struct TaggedPerson
    {
        public string Name;
        public bool HasBox;
        public float X;
        public float Y;
        public float W;
        public float H;

        public bool HasDetails;
        public string Uuid;
        public string AvatarName;
        public string Platform;
        public float Distance;
        public Vector3 Position;
    }

    public class PhotoMetadata
    {
        public int ImageWidth;
        public int ImageHeight;

        public List<TaggedPerson> People = new List<TaggedPerson>();

        public bool HasCamera;
        public float FocalLengthMm;
        public float FNumber;
        public float ExposureSeconds;
        public int Iso;
        public float SensorWidthMm;
        public float SensorHeightMm;
        public float FieldOfView;

        public bool HasCaptureInfo;
        public string Software;
        public string AppVersion;
        public string UnityVersion;
        public DateTime CaptureTimeLocal;

        public bool HasPhotographer;
        public string PhotographerName;
        public string PhotographerUuid;

        public bool HasWorld;
        public string WorldName;
        public bool HasViewpoint;
        public Vector3 CameraPosition;
        public Quaternion CameraRotation;

        public bool HasPano;
        public bool PanoStereoTopBottom;
        public int PanoFullWidth;
        public int PanoFullHeight;
        public int PanoPerEyeHeight;
        public float PanoHeadingDegrees;

        public bool HasAnything()
        {
            return People.Count > 0 || HasCamera || HasCaptureInfo || HasPhotographer || HasWorld || HasPano;
        }
    }

    // ---------------- Collection ----------------

    public static PhotoMetadata CollectMetadata(Camera captureCamera, Transform ignoreRoot, bool panoramic = false)
    {
        var meta = new PhotoMetadata { CaptureTimeLocal = DateTime.Now };
        if (captureCamera == null)
            return meta;

        bool tagPeopleAllowed = !BasisNetworkModeration.IsCameraCategoryDisallowed(BasisNetworkModeration.CameraPolicy_TagPeople);
        bool details = BasisSettingsDefaults.PhotoEmbedPersonDetails.RawValue
            && !BasisNetworkModeration.IsCameraCategoryDisallowed(BasisNetworkModeration.CameraPolicy_PersonDetails);
        meta.People = tagPeopleAllowed ? CollectTaggedPeople(captureCamera, ignoreRoot, details, panoramic) : new List<TaggedPerson>();

        if (BasisSettingsDefaults.PhotoEmbedCameraSettings.RawValue
            && !BasisNetworkModeration.IsCameraCategoryDisallowed(BasisNetworkModeration.CameraPolicy_CameraExif))
        {
            meta.HasCamera = true;
            meta.FocalLengthMm = captureCamera.focalLength;
            meta.FNumber = captureCamera.aperture;
            meta.ExposureSeconds = captureCamera.shutterSpeed;
            meta.Iso = captureCamera.iso;
            meta.SensorWidthMm = captureCamera.sensorSize.x;
            meta.SensorHeightMm = captureCamera.sensorSize.y;
            meta.FieldOfView = captureCamera.fieldOfView;
        }

        if (BasisSettingsDefaults.PhotoEmbedCaptureInfo.RawValue
            && !BasisNetworkModeration.IsCameraCategoryDisallowed(BasisNetworkModeration.CameraPolicy_CaptureInfo))
        {
            meta.HasCaptureInfo = true;
            meta.Software = "Basis Handheld Camera";
            meta.AppVersion = Application.version;
            meta.UnityVersion = Application.unityVersion;
        }

        if (BasisSettingsDefaults.PhotoEmbedPhotographer.RawValue
            && !BasisNetworkModeration.IsCameraCategoryDisallowed(BasisNetworkModeration.CameraPolicy_Photographer))
        {
            BasisPlayer local = BasisLocalPlayer.Instance;
            if (local != null)
            {
                meta.HasPhotographer = true;
                meta.PhotographerName = SafeName(local);
                meta.PhotographerUuid = local.UUID;
            }
        }

        if (BasisSettingsDefaults.PhotoEmbedWorld.RawValue
            && !BasisNetworkModeration.IsCameraCategoryDisallowed(BasisNetworkModeration.CameraPolicy_World))
        {
            meta.HasWorld = true;
            meta.WorldName = SafeWorldName();
            captureCamera.transform.GetPositionAndRotation(out Vector3 pos, out Quaternion rot);
            meta.CameraPosition = pos;
            meta.CameraRotation = rot;
            meta.HasViewpoint = true;
        }

        return meta;
    }

    private static string SafeWorldName()
    {
        try
        {
            var scene = BasisSceneFactory.BasisScene;
            if (scene != null && scene.BasisBundleDescription != null)
                return scene.BasisBundleDescription.AssetBundleName;
        }
        catch (Exception)
        {
        }
        return null;
    }

    private static string SafeName(IBasisPlayer player)
    {
        string name = player.SafeDisplayName;
        if (string.IsNullOrEmpty(name))
            name = player.DisplayName;
        return name;
    }

    private static List<TaggedPerson> CollectTaggedPeople(Camera captureCamera, Transform ignoreRoot, bool details, bool panoramic)
    {
        var people = new List<TaggedPerson>();
        string mode = BasisSettingsDefaults.PhotoMetadataTagging.RawValue;

        if (string.IsNullOrEmpty(mode) || mode == BasisSettingsDefaults.PhotoTagging_NoOne)
            return people;

        if (mode == BasisSettingsDefaults.PhotoTagging_JustMe)
        {
            BasisPlayer local = BasisLocalPlayer.Instance;
            if (local != null && TryBuildPerson(captureCamera, local, requireBounds: false, details, computeBox: !panoramic, out TaggedPerson self))
                people.Add(self);
            return people;
        }

        Plane[] planes = panoramic ? null : GeometryUtility.CalculateFrustumPlanes(captureCamera);

        // Remote players come from the network registry. The local player is
        // handled once via BasisLocalPlayer.Instance: its networked entry is a
        // separate object (would duplicate) and is absent entirely when solo.
        BasisNetworkPlayer[] players = BasisNetworkPlayer.GetAllPlayers();
        for (int i = 0; i < players.Length; i++)
        {
            BasisNetworkPlayer networkPlayer = players[i];
            if (networkPlayer == null || !networkPlayer.TryGetPlayer(out IBasisPlayer player) || player == null)
                continue;
            if (player.IsLocal)
                continue;
            TryAddVisiblePerson(people, captureCamera, planes, player, ignoreRoot, details, panoramic);
        }

        TryAddVisiblePerson(people, captureCamera, planes, BasisLocalPlayer.Instance, ignoreRoot, details, panoramic);

        return people;
    }

    private static void TryAddVisiblePerson(List<TaggedPerson> people, Camera camera, Plane[] planes, IBasisPlayer player, Transform ignoreRoot, bool details, bool panoramic)
    {
        if (player == null)
            return;
        if (!TryGetWorldBounds(player, out Bounds bounds))
            return;
        if (!panoramic && !GeometryUtility.TestPlanesAABB(planes, bounds))
            return;
        if (!HasLineOfSight(camera, bounds, ignoreRoot))
            return;
        if (TryBuildPerson(camera, player, requireBounds: true, details, computeBox: !panoramic, out TaggedPerson tagged))
            people.Add(tagged);
    }

    private static bool TryBuildPerson(Camera camera, IBasisPlayer player, bool requireBounds, bool details, bool computeBox, out TaggedPerson person)
    {
        person = default;

        string name = SafeName(player);
        if (string.IsNullOrEmpty(name))
            return false;
        person.Name = name;

        bool hasBounds = TryGetWorldBounds(player, out Bounds bounds);
        if (requireBounds && !hasBounds)
            return false;

        if (computeBox && hasBounds && TryComputeBox(camera, bounds, out Rect box))
        {
            person.HasBox = true;
            person.X = box.x;
            person.Y = box.y;
            person.W = box.width;
            person.H = box.height;
        }

        if (details)
        {
            person.HasDetails = true;
            person.Uuid = player.UUID;
            person.Platform = player.PlayerPlatform;
            person.AvatarName = SafeAvatarName(player);
            if (hasBounds)
            {
                person.Position = bounds.center;
                person.Distance = Vector3.Distance(camera.transform.position, bounds.center);
            }
        }
        return true;
    }

    private static string SafeAvatarName(IBasisPlayer player)
    {
        try
        {
            var avatar = player.BasisAvatar;
            if (avatar != null && avatar.BasisBundleDescription != null)
                return avatar.BasisBundleDescription.AssetBundleName;
        }
        catch (Exception)
        {
        }
        return null;
    }

    private static bool TryGetWorldBounds(IBasisPlayer player, out Bounds bounds)
    {
        bounds = default;
        var avatar = player.BasisAvatar;
        if (avatar == null || avatar.Renders == null)
            return false;

        bool has = false;
        for (int i = 0; i < avatar.Renders.Length; i++)
        {
            Renderer renderer = avatar.Renders[i];
            if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy)
                continue;
            if (!(renderer is SkinnedMeshRenderer || renderer is MeshRenderer))
                continue;

            if (!has)
            {
                bounds = renderer.bounds;
                has = true;
            }
            else
            {
                bounds.Encapsulate(renderer.bounds);
            }
        }
        return has;
    }

    private static bool HasLineOfSight(Camera camera, Bounds bounds, Transform ignoreRoot)
    {
        Vector3 center = bounds.center;
        float ey = bounds.extents.y;
        Vector3 up = Vector3.up;

        if (SamplePointVisible(camera, bounds, center, ignoreRoot)) return true;
        if (SamplePointVisible(camera, bounds, center + up * (ey * 0.6f), ignoreRoot)) return true;
        if (SamplePointVisible(camera, bounds, center - up * (ey * 0.6f), ignoreRoot)) return true;
        return false;
    }

    private static bool SamplePointVisible(Camera camera, Bounds selfBounds, Vector3 point, Transform ignoreRoot)
    {
        Vector3 origin = camera.transform.position;
        Vector3 delta = point - origin;
        float dist = delta.magnitude;
        if (dist <= 0.01f)
            return true;

        Vector3 dir = delta / dist;

        // Hits on the player's own avatar are not occluders. Compare by bounds
        // (slightly expanded) so this works regardless of where the avatar's
        // colliders sit in the hierarchy.
        Bounds selfSurface = selfBounds;
        selfSurface.Expand(0.2f);

        RaycastHit[] hits = Physics.RaycastAll(origin, dir, dist - 0.05f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < hits.Length; i++)
        {
            Collider col = hits[i].collider;
            if (col == null)
                continue;
            if (ignoreRoot != null && col.transform.IsChildOf(ignoreRoot))
                continue;
            if (selfSurface.Contains(hits[i].point))
                continue;

            return false;
        }
        return true;
    }

    // Projects the 8 corners of the world AABB and returns the screen-space rect,
    // normalized 0-1 with a top-left origin (image convention).
    private static bool TryComputeBox(Camera camera, Bounds worldBounds, out Rect normalizedTopLeft)
    {
        normalizedTopLeft = default;
        Vector3 center = worldBounds.center;
        Vector3 ext = worldBounds.extents;

        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        int inFront = 0;

        for (int i = 0; i < 8; i++)
        {
            Vector3 corner = new Vector3(
                center.x + (((i & 1) == 0) ? -ext.x : ext.x),
                center.y + (((i & 2) == 0) ? -ext.y : ext.y),
                center.z + (((i & 4) == 0) ? -ext.z : ext.z));

            Vector3 vp = camera.WorldToViewportPoint(corner);
            if (vp.z <= 0f)
                continue;

            inFront++;
            if (vp.x < minX) minX = vp.x;
            if (vp.x > maxX) maxX = vp.x;
            if (vp.y < minY) minY = vp.y;
            if (vp.y > maxY) maxY = vp.y;
        }

        if (inFront == 0)
            return false;

        minX = Mathf.Clamp01(minX);
        maxX = Mathf.Clamp01(maxX);
        minY = Mathf.Clamp01(minY);
        maxY = Mathf.Clamp01(maxY);

        float w = maxX - minX;
        float h = maxY - minY;
        if (w <= 0.0001f || h <= 0.0001f)
            return false;

        normalizedTopLeft = new Rect(minX, 1f - maxY, w, h);
        return true;
    }

    // ---------------- Embedding ----------------

    public static byte[] Embed(byte[] imageData, string captureFormat, PhotoMetadata meta, int width, int height)
    {
        if (imageData == null || meta == null || !meta.HasAnything())
            return imageData;

        meta.ImageWidth = width;
        meta.ImageHeight = height;

        string summary = BuildHumanSummary(meta);
        string json = BuildBasisJson(meta);
        string xmp = BuildXmp(meta, summary);

        try
        {
            if (string.Equals(captureFormat, "EXR", StringComparison.OrdinalIgnoreCase))
                return EmbedExr(imageData, meta, summary, json);

            return EmbedPng(imageData, summary, json, xmp);
        }
        catch (Exception ex)
        {
            BasisDebug.LogWarning($"[PhotoMetadata] Could not embed photo metadata: {ex.Message}");
            return imageData;
        }
    }

    private static string BuildHumanSummary(PhotoMetadata meta)
    {
        var sb = new StringBuilder();
        if (meta.People.Count > 0)
        {
            sb.Append("People in photo: ");
            for (int i = 0; i < meta.People.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(meta.People[i].Name);
            }
        }
        if (meta.HasWorld && !string.IsNullOrEmpty(meta.WorldName))
        {
            if (sb.Length > 0) sb.Append(" · ");
            sb.Append("World: ").Append(meta.WorldName);
        }
        if (meta.HasCaptureInfo)
        {
            if (sb.Length > 0) sb.Append(" · ");
            sb.Append("Captured with Basis");
            if (!string.IsNullOrEmpty(meta.AppVersion)) sb.Append(' ').Append(meta.AppVersion);
        }
        return sb.ToString();
    }

    // ---------------- JSON ----------------

    private static string BuildBasisJson(PhotoMetadata meta)
    {
        var sb = new StringBuilder();
        sb.Append('{');
        sb.Append("\"image\":{\"w\":").Append(meta.ImageWidth).Append(",\"h\":").Append(meta.ImageHeight).Append('}');

        if (meta.HasCaptureInfo)
        {
            sb.Append(",\"capture\":{");
            sb.Append("\"software\":\"").Append(EscapeJson(meta.Software)).Append('"');
            if (!string.IsNullOrEmpty(meta.AppVersion)) sb.Append(",\"appVersion\":\"").Append(EscapeJson(meta.AppVersion)).Append('"');
            if (!string.IsNullOrEmpty(meta.UnityVersion)) sb.Append(",\"unityVersion\":\"").Append(EscapeJson(meta.UnityVersion)).Append('"');
            sb.Append(",\"date\":\"").Append(meta.CaptureTimeLocal.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)).Append('"');
            sb.Append('}');
        }

        if (meta.HasPhotographer)
        {
            sb.Append(",\"photographer\":{\"name\":\"").Append(EscapeJson(meta.PhotographerName)).Append('"');
            if (!string.IsNullOrEmpty(meta.PhotographerUuid)) sb.Append(",\"uuid\":\"").Append(EscapeJson(meta.PhotographerUuid)).Append('"');
            sb.Append('}');
        }

        if (meta.HasCamera)
        {
            sb.Append(",\"camera\":{");
            sb.Append("\"focalLengthMm\":").Append(Num(meta.FocalLengthMm));
            sb.Append(",\"fNumber\":").Append(Num(meta.FNumber));
            sb.Append(",\"exposureSeconds\":").Append(Num(meta.ExposureSeconds));
            sb.Append(",\"iso\":").Append(meta.Iso);
            sb.Append(",\"sensorMm\":[").Append(Num(meta.SensorWidthMm)).Append(',').Append(Num(meta.SensorHeightMm)).Append(']');
            sb.Append(",\"fieldOfView\":").Append(Num(meta.FieldOfView));
            sb.Append('}');
        }

        if (meta.HasWorld)
        {
            sb.Append(",\"world\":{");
            sb.Append("\"name\":\"").Append(EscapeJson(meta.WorldName ?? string.Empty)).Append('"');
            if (meta.HasViewpoint)
            {
                sb.Append(",\"cameraPosition\":").Append(Vec3(meta.CameraPosition));
                Vector3 e = meta.CameraRotation.eulerAngles;
                sb.Append(",\"cameraRotationEuler\":").Append(Vec3(e));
            }
            sb.Append('}');
        }

        sb.Append(",\"people\":[");
        for (int i = 0; i < meta.People.Count; i++)
        {
            if (i > 0) sb.Append(',');
            TaggedPerson p = meta.People[i];
            sb.Append("{\"name\":\"").Append(EscapeJson(p.Name)).Append('"');
            if (p.HasBox)
            {
                sb.Append(",\"box\":[")
                  .Append(Num(p.X)).Append(',').Append(Num(p.Y)).Append(',')
                  .Append(Num(p.W)).Append(',').Append(Num(p.H)).Append(']');
            }
            if (p.HasDetails)
            {
                if (!string.IsNullOrEmpty(p.Uuid)) sb.Append(",\"uuid\":\"").Append(EscapeJson(p.Uuid)).Append('"');
                if (!string.IsNullOrEmpty(p.AvatarName)) sb.Append(",\"avatar\":\"").Append(EscapeJson(p.AvatarName)).Append('"');
                if (!string.IsNullOrEmpty(p.Platform)) sb.Append(",\"platform\":\"").Append(EscapeJson(p.Platform)).Append('"');
                if (p.HasBox) sb.Append(",\"distanceM\":").Append(Num(p.Distance)).Append(",\"position\":").Append(Vec3(p.Position));
            }
            sb.Append('}');
        }
        sb.Append(']');

        sb.Append('}');
        return sb.ToString();
    }

    private static string Vec3(Vector3 v)
    {
        return "[" + Num(v.x) + "," + Num(v.y) + "," + Num(v.z) + "]";
    }

    private static string Num(float v) => v.ToString("0.####", CultureInfo.InvariantCulture);

    private static string EscapeJson(string value)
    {
        if (value == null) return string.Empty;
        var sb = new StringBuilder(value.Length + 8);
        foreach (char c in value)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    // ---------------- XMP ----------------

    private static string BuildXmp(PhotoMetadata meta, string summary)
    {
        var sb = new StringBuilder();
        sb.Append("<?xpacket begin=\"﻿\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>");
        sb.Append("<x:xmpmeta xmlns:x=\"adobe:ns:meta/\">");
        sb.Append("<rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">");
        sb.Append("<rdf:Description rdf:about=\"\"");
        sb.Append(" xmlns:dc=\"http://purl.org/dc/elements/1.1/\"");
        sb.Append(" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\"");
        sb.Append(" xmlns:tiff=\"http://ns.adobe.com/tiff/1.0/\"");
        sb.Append(" xmlns:exif=\"http://ns.adobe.com/exif/1.0/\"");
        sb.Append(" xmlns:mwg-rs=\"http://www.metadataworkinggroup.com/schemas/regions/\"");
        sb.Append(" xmlns:stArea=\"http://ns.adobe.com/xmp/sType/Area#\"");
        sb.Append(" xmlns:stDim=\"http://ns.adobe.com/xap/1.0/sType/Dimensions#\"");
        sb.Append(" xmlns:GPano=\"http://ns.google.com/photos/1.0/panorama/\"");
        sb.Append(" xmlns:basis=\"https://basisvr.org/ns/photo/1.0/\">");

        sb.Append("<xmp:CreatorTool>Basis Handheld Camera</xmp:CreatorTool>");
        sb.Append("<xmp:CreateDate>").Append(meta.CaptureTimeLocal.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)).Append("</xmp:CreateDate>");
        sb.Append("<tiff:Make>BasisVR</tiff:Make>");
        sb.Append("<tiff:Model>Handheld Camera</tiff:Model>");

        if (!string.IsNullOrEmpty(summary))
            sb.Append("<dc:description><rdf:Alt><rdf:li xml:lang=\"x-default\">").Append(EscapeXml(summary)).Append("</rdf:li></rdf:Alt></dc:description>");

        if (meta.HasPhotographer && !string.IsNullOrEmpty(meta.PhotographerName))
            sb.Append("<dc:creator><rdf:Seq><rdf:li>").Append(EscapeXml(meta.PhotographerName)).Append("</rdf:li></rdf:Seq></dc:creator>");

        if (meta.HasCamera)
        {
            sb.Append("<exif:FNumber>").Append(Rational(meta.FNumber)).Append("</exif:FNumber>");
            sb.Append("<exif:ExposureTime>").Append(ExposureRational(meta.ExposureSeconds)).Append("</exif:ExposureTime>");
            sb.Append("<exif:FocalLength>").Append(Rational(meta.FocalLengthMm)).Append("</exif:FocalLength>");
            sb.Append("<exif:ISOSpeedRatings><rdf:Seq><rdf:li>").Append(meta.Iso).Append("</rdf:li></rdf:Seq></exif:ISOSpeedRatings>");
            sb.Append("<exif:PixelXDimension>").Append(meta.ImageWidth).Append("</exif:PixelXDimension>");
            sb.Append("<exif:PixelYDimension>").Append(meta.ImageHeight).Append("</exif:PixelYDimension>");
        }

        if (meta.HasWorld)
        {
            if (!string.IsNullOrEmpty(meta.WorldName))
                sb.Append("<basis:World>").Append(EscapeXml(meta.WorldName)).Append("</basis:World>");
            if (meta.HasViewpoint)
            {
                sb.Append("<basis:CameraPosition>").Append(XmlVec3(meta.CameraPosition)).Append("</basis:CameraPosition>");
                sb.Append("<basis:CameraRotation>").Append(XmlVec3(meta.CameraRotation.eulerAngles)).Append("</basis:CameraRotation>");
            }
        }

        AppendPano(sb, meta);
        AppendRegions(sb, meta);

        sb.Append("</rdf:Description></rdf:RDF></x:xmpmeta>");
        sb.Append("<?xpacket end=\"w\"?>");
        return sb.ToString();
    }

    private static void AppendPano(StringBuilder sb, PhotoMetadata meta)
    {
        if (!meta.HasPano)
            return;

        int panoWidth = meta.PanoFullWidth > 0 ? meta.PanoFullWidth : meta.ImageWidth;
        int eyeHeight = meta.PanoPerEyeHeight > 0 ? meta.PanoPerEyeHeight : meta.ImageHeight;

        sb.Append("<GPano:UsePanoramaViewer>True</GPano:UsePanoramaViewer>");
        sb.Append("<GPano:ProjectionType>equirectangular</GPano:ProjectionType>");
        sb.Append("<GPano:FullPanoWidthPixels>").Append(panoWidth).Append("</GPano:FullPanoWidthPixels>");
        sb.Append("<GPano:FullPanoHeightPixels>").Append(eyeHeight).Append("</GPano:FullPanoHeightPixels>");
        sb.Append("<GPano:CroppedAreaImageWidthPixels>").Append(panoWidth).Append("</GPano:CroppedAreaImageWidthPixels>");
        sb.Append("<GPano:CroppedAreaImageHeightPixels>").Append(eyeHeight).Append("</GPano:CroppedAreaImageHeightPixels>");
        sb.Append("<GPano:CroppedAreaLeftPixels>0</GPano:CroppedAreaLeftPixels>");
        sb.Append("<GPano:CroppedAreaTopPixels>0</GPano:CroppedAreaTopPixels>");
        sb.Append("<GPano:InitialViewHeadingDegrees>").Append(Mathf.RoundToInt(meta.PanoHeadingDegrees)).Append("</GPano:InitialViewHeadingDegrees>");

        if (meta.PanoStereoTopBottom)
        {
            sb.Append("<basis:StereoMode>top-bottom</basis:StereoMode>");
            sb.Append("<basis:StereoEyeOrder>left-top</basis:StereoEyeOrder>");
        }
    }

    private static void AppendRegions(StringBuilder sb, PhotoMetadata meta)
    {
        bool any = false;
        for (int i = 0; i < meta.People.Count; i++)
        {
            if (meta.People[i].HasBox) { any = true; break; }
        }
        if (!any)
            return;

        sb.Append("<mwg-rs:Regions rdf:parseType=\"Resource\">");
        sb.Append("<mwg-rs:AppliedToDimensions stDim:w=\"").Append(meta.ImageWidth)
          .Append("\" stDim:h=\"").Append(meta.ImageHeight).Append("\" stDim:unit=\"pixel\"/>");
        sb.Append("<mwg-rs:RegionList><rdf:Bag>");
        for (int i = 0; i < meta.People.Count; i++)
        {
            TaggedPerson p = meta.People[i];
            if (!p.HasBox)
                continue;

            float cx = p.X + p.W * 0.5f;
            float cy = p.Y + p.H * 0.5f;
            sb.Append("<rdf:li rdf:parseType=\"Resource\">");
            sb.Append("<mwg-rs:Name>").Append(EscapeXml(p.Name)).Append("</mwg-rs:Name>");
            sb.Append("<mwg-rs:Type>Face</mwg-rs:Type>");
            sb.Append("<mwg-rs:Area stArea:x=\"").Append(Num(cx))
              .Append("\" stArea:y=\"").Append(Num(cy))
              .Append("\" stArea:w=\"").Append(Num(p.W))
              .Append("\" stArea:h=\"").Append(Num(p.H))
              .Append("\" stArea:unit=\"normalized\"/>");
            sb.Append("</rdf:li>");
        }
        sb.Append("</rdf:Bag></mwg-rs:RegionList>");
        sb.Append("</mwg-rs:Regions>");
    }

    private static string XmlVec3(Vector3 v)
    {
        return Num(v.x) + " " + Num(v.y) + " " + Num(v.z);
    }

    private static string Rational(float value)
    {
        if (value <= 0f)
            return "0/1";
        long den = 1000;
        long num = (long)Math.Round(value * den);
        long g = Gcd(num, den);
        return (num / g) + "/" + (den / g);
    }

    private static string ExposureRational(float seconds)
    {
        if (seconds <= 0f)
            return "0/1";
        if (seconds >= 1f)
            return Rational(seconds);
        long den = (long)Math.Round(1f / seconds);
        if (den <= 0) den = 1;
        return "1/" + den;
    }

    private static long Gcd(long a, long b)
    {
        a = Math.Abs(a);
        b = Math.Abs(b);
        while (b != 0) { long t = b; b = a % b; a = t; }
        return a == 0 ? 1 : a;
    }

    private static string EscapeXml(string value)
    {
        if (value == null) return string.Empty;
        var sb = new StringBuilder(value.Length + 8);
        foreach (char c in value)
        {
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                case '\'': sb.Append("&apos;"); break;
                default:
                    if (c < 0x20 && c != '\t' && c != '\n' && c != '\r') sb.Append(' ');
                    else sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    // ---------------- PNG ----------------

    private static readonly byte[] PngSignature = { 137, 80, 78, 71, 13, 10, 26, 10 };

    private static byte[] EmbedPng(byte[] png, string summary, string json, string xmp)
    {
        if (png.Length < PngSignature.Length + 12)
            return png;
        for (int i = 0; i < PngSignature.Length; i++)
        {
            if (png[i] != PngSignature[i])
                return png;
        }

        int iendStart = -1;
        int pos = PngSignature.Length;
        while (pos + 8 <= png.Length)
        {
            long length = ReadUInt32BE(png, pos);
            if (length < 0 || pos + 12 + length > png.Length)
                break;

            if (png[pos + 4] == 'I' && png[pos + 5] == 'E' && png[pos + 6] == 'N' && png[pos + 7] == 'D')
            {
                iendStart = pos;
                break;
            }
            pos += (int)(12 + length);
        }
        if (iendStart < 0)
            return png;

        var chunks = new List<byte[]>();
        if (!string.IsNullOrEmpty(xmp)) chunks.Add(BuildITxtChunk("XML:com.adobe.xmp", xmp));
        if (!string.IsNullOrEmpty(summary)) chunks.Add(BuildITxtChunk("Description", summary));
        chunks.Add(BuildITxtChunk("BasisPeople", json));

        int extra = 0;
        for (int i = 0; i < chunks.Count; i++) extra += chunks[i].Length;

        byte[] result = new byte[png.Length + extra];
        int o = 0;
        Buffer.BlockCopy(png, 0, result, o, iendStart); o += iendStart;
        for (int i = 0; i < chunks.Count; i++)
        {
            Buffer.BlockCopy(chunks[i], 0, result, o, chunks[i].Length);
            o += chunks[i].Length;
        }
        Buffer.BlockCopy(png, iendStart, result, o, png.Length - iendStart);
        return result;
    }

    // iTXt data: keyword + 0x00 + compFlag(0) + compMethod(0) + langTag + 0x00 + transKeyword + 0x00 + UTF-8 text
    private static byte[] BuildITxtChunk(string keyword, string text)
    {
        byte[] keywordBytes = Encoding.ASCII.GetBytes(keyword);
        byte[] textBytes = Encoding.UTF8.GetBytes(text ?? string.Empty);

        byte[] data = new byte[keywordBytes.Length + 5 + textBytes.Length];
        int p = 0;
        Buffer.BlockCopy(keywordBytes, 0, data, p, keywordBytes.Length); p += keywordBytes.Length;
        data[p++] = 0; // keyword terminator
        data[p++] = 0; // compression flag
        data[p++] = 0; // compression method
        data[p++] = 0; // empty language tag terminator
        data[p++] = 0; // empty translated keyword terminator
        Buffer.BlockCopy(textBytes, 0, data, p, textBytes.Length);

        byte[] type = { (byte)'i', (byte)'T', (byte)'X', (byte)'t' };

        byte[] chunk = new byte[12 + data.Length];
        int c = 0;
        WriteUInt32BE(chunk, c, (uint)data.Length); c += 4;
        Buffer.BlockCopy(type, 0, chunk, c, 4); c += 4;
        Buffer.BlockCopy(data, 0, chunk, c, data.Length); c += data.Length;
        WriteUInt32BE(chunk, c, Crc32(type, data));
        return chunk;
    }

    private static long ReadUInt32BE(byte[] data, int offset)
    {
        return ((long)data[offset] << 24) | ((long)data[offset + 1] << 16) | ((long)data[offset + 2] << 8) | data[offset + 3];
    }

    private static void WriteUInt32BE(byte[] data, int offset, uint value)
    {
        data[offset] = (byte)((value >> 24) & 0xFF);
        data[offset + 1] = (byte)((value >> 16) & 0xFF);
        data[offset + 2] = (byte)((value >> 8) & 0xFF);
        data[offset + 3] = (byte)(value & 0xFF);
    }

    private static uint[] _crcTable;

    private static uint Crc32(byte[] type, byte[] data)
    {
        if (_crcTable == null)
        {
            _crcTable = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++)
                    c = ((c & 1) != 0) ? (0xEDB88320u ^ (c >> 1)) : (c >> 1);
                _crcTable[n] = c;
            }
        }

        uint crc = 0xFFFFFFFFu;
        for (int i = 0; i < type.Length; i++)
            crc = _crcTable[(crc ^ type[i]) & 0xFF] ^ (crc >> 8);
        for (int i = 0; i < data.Length; i++)
            crc = _crcTable[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }

    // ---------------- EXR ----------------

    private static byte[] EmbedExr(byte[] exr, PhotoMetadata meta, string summary, string json)
    {
        if (exr.Length < 8)
            return exr;
        if (exr[0] != 0x76 || exr[1] != 0x2f || exr[2] != 0x31 || exr[3] != 0x01)
            return exr;

        int flags = exr[5] | (exr[6] << 8) | (exr[7] << 16);
        const int Tiled = 0x200;
        const int Deep = 0x800;
        const int Multipart = 0x1000;
        if ((flags & (Tiled | Deep | Multipart)) != 0)
            return exr;

        int p = 8;
        int compression = -1;
        int yMin = 0, yMax = -1;
        bool haveDataWindow = false;

        while (true)
        {
            if (p >= exr.Length)
                return exr;
            if (exr[p] == 0) { p++; break; }

            int nameStart = p;
            while (p < exr.Length && exr[p] != 0) p++;
            if (p >= exr.Length) return exr;
            string attrName = Encoding.ASCII.GetString(exr, nameStart, p - nameStart);
            p++;

            int typeStart = p;
            while (p < exr.Length && exr[p] != 0) p++;
            if (p >= exr.Length) return exr;
            string attrType = Encoding.ASCII.GetString(exr, typeStart, p - typeStart);
            p++;

            if (p + 4 > exr.Length) return exr;
            int attrSize = exr[p] | (exr[p + 1] << 8) | (exr[p + 2] << 16) | (exr[p + 3] << 24);
            p += 4;
            if (attrSize < 0 || p + attrSize > exr.Length) return exr;

            if (attrName == "compression" && attrSize >= 1)
                compression = exr[p];
            else if (attrName == "dataWindow" && attrType == "box2i" && attrSize >= 16)
            {
                yMin = exr[p + 4] | (exr[p + 5] << 8) | (exr[p + 6] << 16) | (exr[p + 7] << 24);
                yMax = exr[p + 12] | (exr[p + 13] << 8) | (exr[p + 14] << 16) | (exr[p + 15] << 24);
                haveDataWindow = true;
            }

            p += attrSize;
        }

        int headerTerminator = p - 1;
        int offsetTableStart = p;

        if (!haveDataWindow || compression < 0)
            return exr;

        int linesPerBlock = LinesPerBlock(compression);
        if (linesPerBlock <= 0)
            return exr;

        int height = yMax - yMin + 1;
        if (height <= 0)
            return exr;

        int chunkCount = (height + linesPerBlock - 1) / linesPerBlock;
        long offsetTableBytes = (long)chunkCount * 8;
        if (offsetTableStart + offsetTableBytes > exr.Length)
            return exr;

        // The first chunk must start immediately after the offset table; if it
        // doesn't, our derived chunk count is wrong, so leave the file untouched.
        long firstOffset = ReadInt64LE(exr, offsetTableStart);
        if (firstOffset != offsetTableStart + offsetTableBytes)
            return exr;

        var attrs = new List<byte[]>();
        if (!string.IsNullOrEmpty(summary)) attrs.Add(BuildExrStringAttribute("comments", summary));
        if (meta.HasCaptureInfo)
        {
            attrs.Add(BuildExrStringAttribute("capDate", meta.CaptureTimeLocal.ToString("yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture)));
            attrs.Add(BuildExrStringAttribute("software", "Basis Handheld Camera" + (string.IsNullOrEmpty(meta.AppVersion) ? "" : " " + meta.AppVersion)));
        }
        if (meta.HasPhotographer && !string.IsNullOrEmpty(meta.PhotographerName))
            attrs.Add(BuildExrStringAttribute("owner", meta.PhotographerName));
        if (meta.HasPano)
            attrs.Add(BuildExrStringAttribute("basisPano", "projection=equirectangular;stereo=" + (meta.PanoStereoTopBottom ? "top-bottom" : "none") + ";heading=" + Num(meta.PanoHeadingDegrees)));
        attrs.Add(BuildExrStringAttribute("BasisPeople", json));

        int delta = 0;
        for (int i = 0; i < attrs.Count; i++) delta += attrs[i].Length;

        byte[] result = new byte[exr.Length + delta];
        int o = 0;
        Buffer.BlockCopy(exr, 0, result, o, headerTerminator); o += headerTerminator;
        for (int i = 0; i < attrs.Count; i++)
        {
            Buffer.BlockCopy(attrs[i], 0, result, o, attrs[i].Length);
            o += attrs[i].Length;
        }
        Buffer.BlockCopy(exr, headerTerminator, result, o, exr.Length - headerTerminator);

        int newOffsetTableStart = offsetTableStart + delta;
        for (int i = 0; i < chunkCount; i++)
        {
            int entryPos = newOffsetTableStart + i * 8;
            WriteInt64LE(result, entryPos, ReadInt64LE(result, entryPos) + delta);
        }
        return result;
    }

    private static int LinesPerBlock(int compression)
    {
        switch (compression)
        {
            case 0: return 1;   // none
            case 1: return 1;   // RLE
            case 2: return 1;   // ZIPS
            case 3: return 16;  // ZIP
            case 4: return 32;  // PIZ
            case 5: return 16;  // PXR24
            case 6: return 32;  // B44
            case 7: return 32;  // B44A
            case 8: return 32;  // DWAA
            case 9: return 256; // DWAB
            default: return 0;
        }
    }

    // EXR attribute: name + 0x00 + "string" + 0x00 + int32 size + UTF-8 value
    private static byte[] BuildExrStringAttribute(string name, string value)
    {
        byte[] nameBytes = Encoding.ASCII.GetBytes(name);
        byte[] typeBytes = Encoding.ASCII.GetBytes("string");
        byte[] valueBytes = Encoding.UTF8.GetBytes(value ?? string.Empty);

        byte[] attr = new byte[nameBytes.Length + 1 + typeBytes.Length + 1 + 4 + valueBytes.Length];
        int p = 0;
        Buffer.BlockCopy(nameBytes, 0, attr, p, nameBytes.Length); p += nameBytes.Length;
        attr[p++] = 0;
        Buffer.BlockCopy(typeBytes, 0, attr, p, typeBytes.Length); p += typeBytes.Length;
        attr[p++] = 0;
        attr[p++] = (byte)(valueBytes.Length & 0xFF);
        attr[p++] = (byte)((valueBytes.Length >> 8) & 0xFF);
        attr[p++] = (byte)((valueBytes.Length >> 16) & 0xFF);
        attr[p++] = (byte)((valueBytes.Length >> 24) & 0xFF);
        Buffer.BlockCopy(valueBytes, 0, attr, p, valueBytes.Length);
        return attr;
    }

    private static long ReadInt64LE(byte[] data, int offset)
    {
        long v = 0;
        for (int i = 0; i < 8; i++)
            v |= (long)data[offset + i] << (8 * i);
        return v;
    }

    private static void WriteInt64LE(byte[] data, int offset, long value)
    {
        for (int i = 0; i < 8; i++)
            data[offset + i] = (byte)((value >> (8 * i)) & 0xFF);
    }
}
