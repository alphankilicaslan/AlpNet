using NAudio.Wave;
using System;

namespace AlpNetPulse
{
    public class SurroundToStereoProvider : ISampleProvider
    {
        private readonly ISampleProvider source;
        private readonly WaveFormat waveFormat;
        private readonly int sourceChannels;

        public SurroundToStereoProvider(ISampleProvider source)
        {
            this.source = source;
            this.sourceChannels = source.WaveFormat.Channels;
            this.waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 2);
        }

        public WaveFormat WaveFormat => waveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            int sourceFrames = count / 2;
            float[] sourceBuffer = new float[sourceFrames * sourceChannels];
            int sourceRead = source.Read(sourceBuffer, 0, sourceFrames * sourceChannels);
            int framesRead = sourceRead / sourceChannels;

            for (int i = 0; i < framesRead; i++)
            {
                int srcIdx = i * sourceChannels;
                int dstIdx = offset + (i * 2);

                // Mix left channels to left, right channels to right
                float left = 0;
                float right = 0;

                if (sourceChannels >= 2)
                {
                    left = sourceBuffer[srcIdx];     // FL
                    right = sourceBuffer[srcIdx + 1]; // FR
                }
                else if (sourceChannels == 1)
                {
                    left = sourceBuffer[srcIdx];
                    right = sourceBuffer[srcIdx];
                }

                // If 5.1 or 7.1, mix center to both, and surround appropriately
                if (sourceChannels >= 6)
                {
                    float center = sourceBuffer[srcIdx + 2] * 0.707f;
                    // LFE = srcIdx + 3 (skip or add small amount)
                    float bl = sourceBuffer[srcIdx + 4]; // Back Left
                    float br = sourceBuffer[srcIdx + 5]; // Back Right
                    
                    left += center + bl;
                    right += center + br;

                    if (sourceChannels >= 8)
                    {
                        float sl = sourceBuffer[srcIdx + 6]; // Side Left
                        float sr = sourceBuffer[srcIdx + 7]; // Side Right
                        left += sl;
                        right += sr;
                    }
                }
                
                // Hard limit to avoid clipping
                buffer[dstIdx] = Math.Max(-1.0f, Math.Min(1.0f, left));
                buffer[dstIdx + 1] = Math.Max(-1.0f, Math.Min(1.0f, right));
            }

            return framesRead * 2;
        }
    }
}
