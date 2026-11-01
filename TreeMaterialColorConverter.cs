using System.Numerics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace OfflineCrpConverter;

internal static class TreeMaterialColorConverter
{
    // Static PBR appearance policy, not a reconstruction of the game's lighting.
    // Parameters depend only on visible texels, never an asset name, package ID or preset file.
    private const int HistogramBins = 4096;
    private const float TargetMedianIntensity = 0.14f;
    private const float ShoulderIntensity = 0.30f;
    private const float MinimumLogLuminance = -20f;
    private static readonly Vector3 LuminanceWeights = new(0.2126f, 0.7152f, 0.0722f);
    private static readonly float[] SrgbToLinear = Enumerable.Range(0, 256)
        .Select(static channel => DecodeSrgb(channel / 255f)).ToArray();

    internal sealed record Analysis(float ShadowGamma, float Saturation,
        float SourceMedianIntensity, long VisibleTexels);

    public static Analysis Analyze(Image<Rgba32> image, bool sourceLinear)
    {
        var intensityHistogram = new long[HistogramBins];
        var chromaHistogram = new long[256];
        long visible = 0;
        long chromatic = 0;
        image.ProcessPixelRows(rows =>
        {
            for (var y = 0; y < rows.Height; y++)
            {
                foreach (var pixel in rows.GetRowSpan(y))
                {
                    if (pixel.A < 128)
                    {
                        continue;
                    }

                    var rgb = ReadLinearRgb(pixel, sourceLinear);
                    // HSV value rather than Rec.709 luminance: equal luminance would
                    // force saturated red leaves much closer to channel clipping than green.
                    intensityHistogram[LuminanceBin(MathF.Max(rgb.X, MathF.Max(rgb.Y, rgb.Z)))]++;
                    visible++;
                    var encoded = sourceLinear
                        ? new Vector3(EncodeSrgb(rgb.X), EncodeSrgb(rgb.Y), EncodeSrgb(rgb.Z))
                        : new Vector3(pixel.R, pixel.G, pixel.B) / 255f;
                    var maximum = MathF.Max(encoded.X, MathF.Max(encoded.Y, encoded.Z));
                    var minimum = MathF.Min(encoded.X, MathF.Min(encoded.Y, encoded.Z));
                    var chroma = maximum > 0f ? (maximum - minimum) / maximum : 0f;
                    if (chroma >= 0.08f)
                    {
                        chromaHistogram[Math.Clamp((int)MathF.Round(chroma * 255f), 0, 255)]++;
                        chromatic++;
                    }
                }
            }
        });

        if (visible == 0)
        {
            throw new InvalidDataException("树木没有通过 alpha 遮罩的可见像素，不能自动适配颜色");
        }

        var medianBin = PercentileIndex(intensityHistogram, visible, 0.5f);
        var median = medianBin == 0 ? 0f : MathF.Pow(2f,
            MinimumLogLuminance + (medianBin - 1f) / (HistogramBins - 2) * -MinimumLogLuminance);
        if (median <= 0f)
        {
            throw new InvalidDataException("树木可见像素的亮度中位数接近零，请检查贴图或显式选择原色/手动校准");
        }

        if (median >= TargetMedianIntensity)
        {
            return new Analysis(1f, 1f, median, visible);
        }

        var saturation = chromatic == 0 ? 1f
            : Math.Clamp(0.50f / (PercentileIndex(chromaHistogram, chromatic, 0.5f) / 255f), 1f, 1.75f);
        var gamma = MathF.Log(TargetMedianIntensity / ShoulderIntensity) /
            MathF.Log(median / ShoulderIntensity);
        return new Analysis(gamma, saturation, median, visible);
    }

    public static void ApplyAutomatic(Image<Rgba32> image, Analysis analysis, bool sourceLinear)
    {
        image.ProcessPixelRows(rows =>
        {
            for (var y = 0; y < rows.Height; y++)
            {
                foreach (ref var pixel in rows.GetRowSpan(y))
                {
                    var source = ReadLinearRgb(pixel, sourceLinear);
                    var rgb = sourceLinear
                        ? new Vector3(EncodeSrgb(source.X), EncodeSrgb(source.Y), EncodeSrgb(source.Z))
                        : new Vector3(pixel.R, pixel.G, pixel.B) / 255f;
                    var gray = Vector3.Dot(rgb, LuminanceWeights);
                    var minimum = MathF.Min(rgb.X, MathF.Min(rgb.Y, rgb.Z));
                    // Bound saturation before scaling so RGB hue is not changed by channel clipping.
                    var saturation = gray > minimum ? MathF.Min(analysis.Saturation, gray / (gray - minimum)) : 1f;
                    rgb = Vector3.Max(ChangeSaturation(rgb, saturation), Vector3.Zero);
                    var maximum = MathF.Max(rgb.X, MathF.Max(rgb.Y, rgb.Z));
                    if (maximum > 1f)
                    {
                        rgb /= maximum;
                    }

                    var linear = new Vector3(DecodeSrgb(rgb.X), DecodeSrgb(rgb.Y), DecodeSrgb(rgb.Z));
                    var intensity = MathF.Max(linear.X, MathF.Max(linear.Y, linear.Z));
                    if (intensity > 0f && intensity < ShoulderIntensity)
                    {
                        var lifted = ShoulderIntensity * MathF.Pow(intensity / ShoulderIntensity, analysis.ShadowGamma);
                        // Value-based sRGB gain preserves hue without over-amplifying red/blue.
                        rgb *= EncodeSrgb(lifted) / EncodeSrgb(intensity);
                    }

                    maximum = MathF.Max(rgb.X, MathF.Max(rgb.Y, rgb.Z));
                    if (maximum > 1f)
                    {
                        rgb /= maximum;
                    }

                    pixel.R = QuantizeByte(rgb.X);
                    pixel.G = QuantizeByte(rgb.Y);
                    pixel.B = QuantizeByte(rgb.Z);
                }
            }
        });
    }

    public static void Apply(Image<Rgba32> image, TreeColorCorrection correction, bool sourceLinear)
    {
        var exposure = MathF.Pow(2f, correction.ExposureStops);
        image.ProcessPixelRows(rows =>
        {
            for (var y = 0; y < rows.Height; y++)
            {
                foreach (ref var pixel in rows.GetRowSpan(y))
                {
                    var rgb = ChangeSaturation(ReadLinearRgb(pixel, sourceLinear), correction.Saturation) * exposure;
                    pixel.R = EncodeByte(rgb.X);
                    pixel.G = EncodeByte(rgb.Y);
                    pixel.B = EncodeByte(rgb.Z);
                }
            }
        });
    }

    private static Vector3 ReadLinearRgb(Rgba32 pixel, bool sourceLinear)
        => sourceLinear ? new Vector3(pixel.R, pixel.G, pixel.B) / 255f
            : new Vector3(SrgbToLinear[pixel.R], SrgbToLinear[pixel.G], SrgbToLinear[pixel.B]);

    private static Vector3 ChangeSaturation(Vector3 rgb, float saturation)
    {
        var luminance = new Vector3(Vector3.Dot(rgb, LuminanceWeights));
        return luminance + (rgb - luminance) * saturation;
    }

    private static int PercentileIndex(long[] histogram, long count, float fraction)
    {
        var rank = Math.Max(1L, (long)Math.Ceiling(count * (double)fraction));
        long cumulative = 0;
        for (var index = 0; index < histogram.Length; index++)
        {
            cumulative += histogram[index];
            if (cumulative >= rank)
            {
                return index;
            }
        }

        throw new InvalidOperationException("颜色直方图的计数不一致");
    }

    private static int LuminanceBin(float value) => value <= 0f ? 0 : 1 + Math.Clamp(
        (int)MathF.Round((MathF.Log2(value) - MinimumLogLuminance) / -MinimumLogLuminance * (HistogramBins - 2)),
        0, HistogramBins - 2);
    private static float DecodeSrgb(float value) => value <= 0.04045f ? value / 12.92f : MathF.Pow((value + 0.055f) / 1.055f, 2.4f);
    private static float EncodeSrgb(float value)
    {
        value = Math.Clamp(value, 0f, 1f);
        return value <= 0.0031308f ? value * 12.92f : 1.055f * MathF.Pow(value, 1f / 2.4f) - 0.055f;
    }
    private static byte QuantizeByte(float value) => (byte)Math.Clamp((int)MathF.Round(value * 255f), 0, 255);
    private static byte EncodeByte(float value) => QuantizeByte(EncodeSrgb(value));
}
