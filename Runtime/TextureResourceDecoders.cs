using PFound.RemoteResourceCache.Core;
using UnityEngine;

namespace PFound.RemoteResourceCache
{
    /// <summary>
    /// Concrete <see cref="ResourceDecoder{T}"/>s and matching <see cref="ResourceSizer{T}"/>s that turn raw
    /// downloaded bytes into Unity image types. These bridge the engine-free core (which only moves
    /// <c>byte[]</c>) to <see cref="Texture2D"/>/<see cref="Sprite"/> without the core ever referencing
    /// UnityEngine. A decoder that is handed undecodable bytes throws, which the cache reports as
    /// <see cref="ResourceFailureKind.Decode"/>.
    /// </summary>
    public static class TextureResourceDecoders
    {
        /// <summary>Decodes PNG/JPG bytes into a <see cref="Texture2D"/> via <see cref="ImageConversion.LoadImage"/>.</summary>
        public static Texture2D DecodeTexture(byte[] rawBytes)
        {
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: false);
            if (!texture.LoadImage(rawBytes))
            {
                DestroyTexture(texture);
                throw new ResourceCacheException("Bytes are not a decodable image.");
            }
            return texture;
        }

        /// <summary>
        /// Editor-safe texture destruction: <see cref="Object.DestroyImmediate"/> outside play mode
        /// (EditMode tests forbid <see cref="Object.Destroy"/>), <see cref="Object.Destroy"/> at runtime.
        /// </summary>
        private static void DestroyTexture(Texture2D tex)
        {
            if (Application.isPlaying)
                Object.Destroy(tex);
            else
                Object.DestroyImmediate(tex);
        }

        /// <summary>Decodes image bytes into a full-rect <see cref="Sprite"/> at 100 pixels-per-unit.</summary>
        public static Sprite DecodeSprite(byte[] rawBytes)
        {
            Texture2D texture = DecodeTexture(rawBytes);
            return Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(0.5f, 0.5f));
        }

        /// <summary>Approximate GPU footprint of a texture (4 bytes/pixel) for the memory tier's byte budget.</summary>
        public static long SizeOfTexture(Texture2D texture) => (long)texture.width * texture.height * 4L;

        /// <summary>Footprint of a sprite, taken from its backing texture.</summary>
        public static long SizeOfSprite(Sprite sprite) => SizeOfTexture((Texture2D)sprite.texture);
    }
}
