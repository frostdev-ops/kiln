# SPDX-License-Identifier: MIT
"""The layouts of what kiln-vfxcook writes, written out by hand, as a kfc TypeDb.

So the output can be decoded and re-encoded with tools/kfc on a machine without the game. With the game installed, the tests compare every
record here against its reflection (docs/re/vfx.md section 2 has the same tables).
"""
from kfc import refl as R
from kfc.refl import TypeDb
from kfc.util import fnv1a32

# Short names of primitives (kind, size)
_PRIM = {"keen::bool": (R.K_BOOL, 1), "keen::uint8": (R.K_U8, 1), "keen::uint16": (R.K_U16, 2), "keen::uint32": (R.K_U32, 4),
         "keen::uint64": (R.K_U64, 8), "keen::float32": (R.K_F32, 4)}


def _struct(name, size, align, *fields):
    return {"name": name, "hash": fnv1a32(name), "layout": 0, "size": size, "align": align, "kind": R.K_STRUCT, "flags": 0,
            "count": len(fields), "inner": None, "fields": [{"name": n, "type": t, "offset": o} for o, n, t in fields]}


def _enum(name, inner, **values):
    size = _PRIM[inner][1]
    return {"name": name, "hash": fnv1a32(name), "layout": 0, "size": size, "align": size, "kind": R.K_ENUM, "flags": 0,
            "count": len(values), "inner": inner, "values": [{"name": k, "value": v} for k, v in values.items()]}


def _prim(name, kind, size):
    return {"name": name, "hash": fnv1a32(name), "layout": 0, "size": size, "align": size, "kind": kind, "flags": 0, "count": 0, "inner": None}


def _blob_array(inner):
    name = f"keen::BlobArray<{inner}>"
    return {"name": name, "hash": fnv1a32(name), "layout": 0, "size": 8, "align": 4, "kind": R.K_BLOB_ARRAY, "flags": 0, "count": 0, "inner": inner}


def _object_ref(inner):
    name = f"keen::ObjectReference<{inner}>"
    return {"name": name, "hash": fnv1a32(name), "layout": 0, "size": 16, "align": 4, "kind": R.K_OBJECT_REF, "flags": 0, "count": 0, "inner": inner}


def _static_array(inner, count, size):
    name = f"keen::StaticArray<{inner},{count}>"
    return {"name": name, "hash": fnv1a32(name), "layout": 0, "size": count * size, "align": size, "kind": R.K_STATIC_ARRAY, "flags": 0,
            "count": count, "inner": inner}


def types():
    """A TypeDb holding keen::VfxResource, keen::VfxMeshResource, keen::RenderTextureResource and what they name."""
    ts = [_prim(n, k, s) for n, (k, s) in _PRIM.items()]
    string = {"name": "keen::string", "hash": fnv1a32("keen::string"), "layout": 0, "size": 8, "align": 4, "kind": R.K_BLOB_STRING, "flags": 0,
              "count": 0, "inner": None}
    ts.append(string)
    ts += [
        _struct("keen::HashKey32", 4, 4, (0, "value", "keen::uint32")),
        _struct("keen::ContentHash", 16, 4, (0, "size", "keen::uint32"), (4, "hash0", "keen::uint32"), (8, "hash1", "keen::uint32"),
                (12, "hash2", "keen::uint32")),
        _struct("keen::ResourceContentReference", 32, 8, (0, "hash", "keen::ContentHash"), (0x10, "category", "keen::HashKey32"),
                (0x14, "bundleId", "keen::uint32"), (0x18, "sortKey", "keen::uint64")),
        _struct("keen::Range16", 4, 2, (0, "start", "keen::uint16"), (2, "count", "keen::uint16")),
        _struct("keen::Range32", 8, 4, (0, "start", "keen::uint32"), (4, "count", "keen::uint32")),
        _struct("keen::float4", 16, 4, (0, "x", "keen::float32"), (4, "y", "keen::float32"), (8, "z", "keen::float32"), (12, "w", "keen::float32")),
        _struct("keen::VfxPatchPoint", 4, 2, (0, "resourceDataRange", "keen::Range16")),
        _enum("keen::VfxBufferMemberComponentType", "keen::uint8", Float=0, SignedInt=1, UnsignedInt=2, BoolMask=3),
        _enum("keen::RenderSamplerAddressMode", "keen::uint8", Wrap=0, Mirror=1, Clamp=2, ClampToZero=3),
        _enum("keen::TextureType", "keen::uint8", Texture1D=0, Texture2D=1, Texture3D=2, Cube=3),
        _enum("keen::PixelFormat", "keen::uint8", BC1_RGB_unorm_block=131, BC5_unorm_block=141, BC7_unorm_block=145, BC7_srgb_block=146),
        _enum("keen::RenderEffectId", "keen::uint8", Billboard=0, Pbr=1, PbrTerrain=2, PbrBuilding=3, PbrRoof=4, Gizmo=5, Grid=6, VfxGrid=7, Transparent=8,
              TerraformingCursor=9, SkyTexture=10),
        _enum("keen::RenderMaterialFlag", "keen::uint8", Tessellated=0, AlphaTested=1, DoubleSided=2, DisableShadows=3, CanDissolve=4, Translucent=5, Foliage=6, HashedAlpha=7),
        {"name": "keen::Bitmask8<keen::RenderMaterialFlag>", "hash": fnv1a32("keen::Bitmask8<keen::RenderMaterialFlag>"), "layout": 0, "size": 1, "align": 1, "kind": R.K_BM8,
         "flags": 0, "count": 0, "inner": "keen::RenderMaterialFlag"},
        {"name": "keen::RenderMaterialFlagMask", "hash": fnv1a32("keen::RenderMaterialFlagMask"), "layout": 0, "size": 1, "align": 1, "kind": R.K_TYPEDEF, "flags": 0, "count": 0,
         "inner": "keen::Bitmask8<keen::RenderMaterialFlag>"},
        _struct("keen::RenderStreamedTexture", 88, 8, (0, "width", "keen::uint16"), (2, "height", "keen::uint16"), (4, "levelCount", "keen::uint8"), (5, "format", "keen::PixelFormat"),
                (8, "content", "keen::ResourceContentReference"), (0x28, "mipTailContent", "keen::ResourceContentReference"),
                (0x48, "mipChainContentRanges", "keen::BlobArray<keen::Range32>"), (0x50, "debugName", "keen::string")),
        _struct("keen::RenderMaterialImage", 96, 8, (0, "dataOffset", "keen::uint32"), (8, "texture", "keen::RenderStreamedTexture")),
        _struct("keen::RenderMaterialTextureFeedback", 12, 4, (0, "dataOffset", "keen::uint32"), (4, "renderMaterialImageIndices", "keen::BlobArray<keen::uint32>")),
        _struct("keen::RenderMaterialResource", 40, 4, (0, "effectId", "keen::RenderEffectId"), (1, "flags", "keen::RenderMaterialFlagMask"),
                (4, "data", "keen::BlobArray<keen::uint8>"), (0xc, "dataTypeSignature", "keen::HashKey32"), (0x10, "images", "keen::BlobArray<keen::RenderMaterialImage>"),
                (0x18, "textureFeedbacks", "keen::BlobArray<keen::RenderMaterialTextureFeedback>"), (0x20, "debugName", "keen::string")),
        _enum("keen::GraphicsTextureChannel", "keen::uint8", Identity=0, Zero=1, One=2, R=3, G=4, B=5, A=6),
        _struct("keen::GraphicsTextureChannelMapping", 4, 1, (0, "x", "keen::GraphicsTextureChannel"), (1, "y", "keen::GraphicsTextureChannel"),
                (2, "z", "keen::GraphicsTextureChannel"), (3, "w", "keen::GraphicsTextureChannel")),
        _struct("keen::VfxBufferMember", 20, 4, (0, "name", "keen::string"), (8, "componentCount", "keen::uint32"),
                (0xc, "componentType", "keen::VfxBufferMemberComponentType"), (0x10, "offsetInBytes", "keen::uint32")),
        _struct("keen::VfxParticleStateBufferInfo", 12, 4, (0, "members", "keen::BlobArray<keen::VfxBufferMember>"), (8, "sizeInBytes", "keen::uint32")),
        _struct("keen::VfxParticleResource", 104, 8, (0, "debugName", "keen::string"), (8, "depth", "keen::uint16"),
                (0xa, "stateSizeInBytes", "keen::uint16"), (0xc, "parent", "keen::uint32"), (0x10, "child", "keen::uint32"),
                (0x14, "maxCount", "keen::uint32"), (0x18, "code", "keen::ResourceContentReference"),
                (0x38, "strippedCode", "keen::ResourceContentReference"), (0x58, "initialState", "keen::BlobArray<keen::uint8>"),
                (0x60, "initializeStateGpuResourceDataRange", "keen::Range16"), (0x64, "evaluateParametersGpuResourceDataRange", "keen::Range16")),
        _struct("keen::VfxSamplerParameters", 3, 1, (0, "addressU", "keen::RenderSamplerAddressMode"), (1, "addressV", "keen::RenderSamplerAddressMode"),
                (2, "addressW", "keen::RenderSamplerAddressMode")),
        _struct("keen::VfxMeshPatchPoint", 20, 4, (0, "patchPoint", "keen::VfxPatchPoint"), (4, "mesh", "keen::ObjectReference<keen::VfxMeshResource>")),
        _struct("keen::VfxImagePatchPoint", 24, 4, (0, "patchPoint", "keen::VfxPatchPoint"), (4, "image", "keen::ObjectReference<keen::RenderTextureResource>"),
                (0x14, "sampler", "keen::VfxSamplerParameters")),
        _struct("keen::VfxUserEffectParameterDataBlock", 64, 4, (0, "data", "keen::StaticArray<keen::uint32,16>")),
        _struct("keen::VfxResourceReferences", 24, 4, (0, "meshes", "keen::BlobArray<keen::ObjectReference<keen::VfxMeshResource>>"),
                (8, "models", "keen::BlobArray<keen::ObjectReference<keen::RenderModel>>"),
                (0x10, "jointCollections", "keen::BlobArray<keen::ObjectReference<keen::VfxJointCollectionResource>>")),
        _struct("keen::VfxResource", 216, 4, (0, "cullingDistanceMin", "keen::float32"), (4, "cullingDistanceMax", "keen::float32"),
                (8, "killTimeInSeconds", "keen::uint32"), (0xc, "emitterCount", "keen::uint32"), (0x10, "useNewVm", "keen::bool"),
                (0x14, "particles", "keen::BlobArray<keen::VfxParticleResource>"), (0x1c, "stateInfos", "keen::BlobArray<keen::VfxParticleStateBufferInfo>"),
                (0x24, "springs", "keen::BlobArray<keen::VfxSpringResource>"), (0x2c, "gpuResourceData", "keen::BlobArray<keen::uint8>"),
                (0x34, "meshes", "keen::BlobArray<keen::VfxMeshPatchPoint>"), (0x3c, "models", "keen::BlobArray<keen::VfxModelPatchPoint>"),
                (0x44, "decals", "keen::BlobArray<keen::VfxDecalPatchPoint>"), (0x4c, "images", "keen::BlobArray<keen::VfxImagePatchPoint>"),
                (0x54, "lightFilters", "keen::BlobArray<keen::VfxLightFilterPatchPoint>"), (0x5c, "sounds", "keen::BlobArray<keen::VfxSoundPatchPoint>"),
                (0x64, "curves", "keen::BlobArray<keen::VfxCurvePatchPoint>"), (0x6c, "parameters", "keen::BlobArray<keen::VfxUserEffectParameterDescription>"),
                (0x74, "defaultParameterData", "keen::VfxUserEffectParameterDataBlock"),
                (0xb4, "defaultParameterDataResourceReferences", "keen::VfxResourceReferences"),
                (0xcc, "effectJointCollections", "keen::BlobArray<keen::ObjectReference<keen::VfxJointCollectionResource>>"),
                (0xd4, "jointCollectionCount", "keen::uint8")),
        _struct("keen::VfxMeshResource", 56, 8, (0, "indexCount", "keen::uint32"), (4, "vertexCount", "keen::uint32"),
                (8, "meshData", "keen::ResourceContentReference"), (0x28, "boundingSphere", "keen::float4")),
        _struct("keen::RenderTextureResource", 72, 8, (0, "width", "keen::uint16"), (2, "height", "keen::uint16"), (4, "depth", "keen::uint16"),
                (6, "layerCount", "keen::uint16"), (8, "levelCount", "keen::uint8"), (9, "type", "keen::TextureType"), (0xa, "format", "keen::PixelFormat"),
                (0xb, "channelMapping", "keen::GraphicsTextureChannelMapping"), (0x10, "content", "keen::ResourceContentReference"),
                (0x30, "mipChainContentRanges", "keen::BlobArray<keen::Range32>"), (0x38, "bindless", "keen::bool"), (0x39, "isTiled", "keen::bool"),
                (0x3c, "debugName", "keen::string")),
        _static_array("keen::uint32", 16, 4),
        _struct("keen::DsTypeId", 4, 4, (0, "value", "keen::uint32")),
        _struct("keen::Range8", 2, 1, (0, "start", "keen::uint8"), (1, "count", "keen::uint8")),
        # the per-play parameters every Kiln VFX describes (KX_* in kiln_vfx_params.h)
        _struct("keen::VfxUserEffectParameterDescription", 12, 4, (0, "nameHash", "keen::HashKey32"), (4, "typeId", "keen::DsTypeId"),
                (8, "dataRange", "keen::Range8"), (10, "jointCollectionIndex", "keen::uint8")),
        _blob_array("keen::VfxUserEffectParameterDescription"),
        _object_ref("keen::VfxMeshResource"), _object_ref("keen::RenderTextureResource"), _object_ref("keen::RenderModel"),
        _object_ref("keen::VfxJointCollectionResource"),
    ]
    # Arrays the builder leaves empty: their element types only need a size.
    for name, size in (("keen::VfxSpringResource", 184), ("keen::VfxModelPatchPoint", 20), ("keen::VfxDecalPatchPoint", 20),
                       ("keen::VfxLightFilterPatchPoint", 20), ("keen::VfxSoundPatchPoint", 20), ("keen::VfxCurvePatchPoint", 20)):
        ts.append(_struct(name, size, 4))
        ts.append(_blob_array(name))
    for inner in ("keen::VfxParticleResource", "keen::VfxParticleStateBufferInfo", "keen::uint8", "keen::VfxMeshPatchPoint", "keen::VfxImagePatchPoint",
                  "keen::VfxBufferMember", "keen::Range32", "keen::uint32", "keen::RenderMaterialImage", "keen::RenderMaterialTextureFeedback",
                  "keen::ObjectReference<keen::VfxMeshResource>", "keen::ObjectReference<keen::RenderModel>",
                  "keen::ObjectReference<keen::VfxJointCollectionResource>"):
        ts.append(_blob_array(inner))
    # align of element records as reflection has them
    return TypeDb({"types": ts, "count": len(ts)})
