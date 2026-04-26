namespace Unosquare.FFME.Container
{
    using ClosedCaptions;
    using Common;
    using FFmpeg.AutoGen;
    using System.Collections.Generic;

    /// <inheritdoc />
    /// <summary>
    /// A pre-allocated, scaled video block. The buffer is in BGR, 24-bit format.
    /// </summary>
    internal sealed class VideoBlock : MediaBlock
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="VideoBlock" /> class.
        /// </summary>
        internal VideoBlock()
            : base(MediaType.Video)
        {
            // placeholder
        }

        #region Properties

        /// <summary>
        /// Gets the number of horizontal pixels in the image.
        /// </summary>
        public int PixelWidth { get; private set; }

        /// <summary>
        /// Gets the number of vertical pixels in the image.
        /// </summary>
        public int PixelHeight { get; private set; }

        /// <summary>
        /// Gets the pixel aspect width.
        /// This is NOT the display aspect width.
        /// </summary>
        public int PixelAspectWidth { get; internal set; }

        /// <summary>
        /// Gets the pixel aspect height.
        /// This is NOT the display aspect height.
        /// </summary>
        public int PixelAspectHeight { get; internal set; }

        /// <summary>
        /// Gets the SMTPE time code.
        /// </summary>
        public string SmtpeTimeCode { get; internal set; }

        /// <summary>
        /// Gets a value indicating whether this frame was decoded in a hardware context.
        /// </summary>
        public bool IsHardwareFrame { get; internal set; }

        /// <summary>
        /// Gets the name of the hardware decoder if the frame was decoded in a hardware context.
        /// </summary>
        public string HardwareAcceleratorName { get; internal set; }

        /// <summary>
        /// Gets the display picture number (frame number).
        /// If not set by the decoder, this attempts to obtain it by dividing the start time by the
        /// frame duration.
        /// </summary>
        public long DisplayPictureNumber { get; internal set; }

        /// <summary>
        /// Gets the coded picture number set by the decoder.
        /// </summary>
        public long CodedPictureNumber { get; internal set; }

        /// <summary>
        /// Gets the picture type.
        /// </summary>
        public AVPictureType PictureType { get; internal set; }

        /// <summary>
        /// Gets the closed caption packets for this video block.
        /// </summary>
        public IReadOnlyList<ClosedCaptionPacket> ClosedCaptions { get; internal set; }

        /// <summary>
        /// Gets the picture buffer stride.
        /// </summary>
        internal int PictureBufferStride { get; private set; }

        #endregion

        #region Methods

        /// <summary>
        /// Allocates a block of memory suitable for a picture buffer
        /// and sets the corresponding properties.
        /// </summary>
        /// <param name="source">The source.</param>
        /// <param name="pixelFormat">The pixel format.</param>
        /// <returns>True if the allocation was successful.</returns>
        internal unsafe bool Allocate(VideoFrame source, AVPixelFormat pixelFormat)
        {
            // sws_scale's SIMD output kernel writes destination pixels in 16-pixel-wide
            // chunks. When the visible width isn't a multiple of 16, the tail chunk
            // overruns the row-end of an exactly-sized destination buffer by up to 15
            // pixels and corrupts the heap (manifest as 0xc0000374, often only on a
            // later allocation). Over-allocate the row stride to a 16-pixel boundary
            // so the tail writes land in our own padding instead of someone else's
            // memory. PixelWidth still reflects the visible region; the renderer
            // copies row-by-row to skip past the right-edge padding.
            var visibleWidth = source.Pointer->width;
            var visibleHeight = source.Pointer->height;
            var alignedWidth = (visibleWidth + 15) & ~15;

            var targetLength = ffmpeg.av_image_get_buffer_size(pixelFormat, alignedWidth, visibleHeight, 1);
            if (!Allocate(targetLength))
                return false;

            // Update related properties
            PictureBufferStride = ffmpeg.av_image_get_linesize(pixelFormat, alignedWidth, 0);
            PixelWidth = visibleWidth;
            PixelHeight = visibleHeight;

            return true;
        }

        /// <inheritdoc />
        protected override void Deallocate()
        {
            base.Deallocate();
            PictureBufferStride = 0;
            PixelWidth = 0;
            PixelHeight = 0;
        }

        #endregion
    }
}
