using System;
using System.Runtime.InteropServices;
using LiveKit;
using LiveKit.Internal;
using LiveKit.Internal.FFIClients.Requests;
using LiveKit.Proto;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;

namespace ExampleRooms
{
    public class MicrophoneRtcAudioSource : IRtcAudioSource
    {
        private const int DEFAULT_NUM_CHANNELS = 2;
        private const int DEFAULT_SAMPLE_RATE = 48000;
        private readonly AudioBuffer buffer = new();
        private readonly object lockObject = new();

        private readonly AudioSource audioSource;
        private readonly IAudioFilter audioFilter;
        private readonly Apm apm; // Doesn't own APM, Doesn't have to dispose
        private readonly ApmReverseStream? reverseStream;

        public FfiHandle Handle { get; }

        public MicrophoneRtcAudioSource(AudioSource audioSource, IAudioFilter audioFilter, Apm apm)
        {
            reverseStream = ApmReverseStream.NewOrNull(apm);

            using var request = FFIBridge.Instance.NewRequest<NewAudioSourceRequest>();
            var newAudioSource = request.request;
            newAudioSource.Type = AudioSourceType.AudioSourceNative;
            newAudioSource.NumChannels = DEFAULT_NUM_CHANNELS;
            newAudioSource.SampleRate = DEFAULT_SAMPLE_RATE;

            using var options = request.TempResource<AudioSourceOptions>();
            newAudioSource.Options = options;
            newAudioSource.Options.EchoCancellation = true;
            newAudioSource.Options.NoiseSuppression = true;
            newAudioSource.Options.AutoGainControl = true;

            using var response = request.Send();
            FfiResponse res = response;
            Handle = IFfiHandleFactory.Default.NewFfiHandle(res.NewAudioSource.Source.Handle!.Id);
            this.audioSource = audioSource;
            this.audioFilter = audioFilter;
            this.apm = apm;
        }

        public void Start()
        {
            Stop();
            if (!audioFilter?.IsValid == true || !audioSource)
            {
                Debug.LogError("AudioFilter or AudioSource is null - cannot start audio capture");
                return;
            }

            audioFilter.AudioRead += OnAudioRead;
            audioSource.Play();
            reverseStream?.Start();
        }

        public void Stop()
        {
            if (audioFilter?.IsValid == true) audioFilter.AudioRead -= OnAudioRead;
            if (audioSource) audioSource.Stop();
            reverseStream?.Stop();
            
            //TODO IRtcAudioSource must implement dispose method to place this call
            reverseStream?.Dispose();

            lock (lockObject)
            {
                buffer.Dispose();
            }
        }

        private void OnAudioRead(Span<float> data, int channels, int sampleRate)
        {
            lock (lockObject)
            {
                buffer.Write(data, (uint)channels, (uint)sampleRate);
                while (true)
                {
                    var frameResult = buffer.ReadDuration(ApmFrame.FRAME_DURATION_MS);
                    if (frameResult.Has == false) break;
                    using var frame = frameResult.Value;

                    var audioBytes = MemoryMarshal.Cast<byte, PCMSample>(frame.AsSpan());

                    var apmFrame = ApmFrame.New(
                        audioBytes,
                        frame.NumChannels,
                        frame.SamplesPerChannel,
                        new SampleRate(frame.SampleRate),
                        out string? error
                    );
                    if (error != null)
                    {
                        Debug.LogError($"Error during creation ApmFrame: {error}");
                        break;
                    }

                    var apmResult = apm.ProcessStream(apmFrame);
                    if (apmResult.Success == false)
                        Debug.LogError($"Error during processing stream: {apmResult.ErrorMessage}");

                    ProcessAudioFrame(frame);
                }
            }
        }

        private void ProcessAudioFrame(in AudioFrame frame)
        {
            try
            {
                using var request = FFIBridge.Instance.NewRequest<CaptureAudioFrameRequest>();
                using var audioFrameBufferInfo = request.TempResource<AudioFrameBufferInfo>();

                var pushFrame = request.request;
                pushFrame.SourceHandle = (ulong)Handle.DangerousGetHandle();
                pushFrame.Buffer = audioFrameBufferInfo;
                pushFrame.Buffer.DataPtr = (ulong)frame.Data;
                pushFrame.Buffer.NumChannels = frame.NumChannels;
                pushFrame.Buffer.SampleRate = frame.SampleRate;
                pushFrame.Buffer.SamplesPerChannel = frame.SamplesPerChannel;

                using var response = request.Send();

                pushFrame.Buffer.DataPtr = 0;
                pushFrame.Buffer.NumChannels = 0;
                pushFrame.Buffer.SampleRate = 0;
                pushFrame.Buffer.SamplesPerChannel = 0;
            }
            catch (Exception e)
            {
                Debug.LogError("Audio Framedata error: " + e.Message + "\nStackTrace: " + e.StackTrace);
            }
        }
    }

    // Just a copy from livekit, because ctor is internal
    public struct AudioFrame : IDisposable
    {
        public readonly uint NumChannels;
        public readonly uint SampleRate;
        public readonly uint SamplesPerChannel;

        private readonly NativeArray<byte> _data;
        private readonly IntPtr _dataPtr;
        private bool _disposed;

        public IntPtr Data => _dataPtr;
        public int Length => (int)(SamplesPerChannel * NumChannels * sizeof(short));
        public bool IsValid => _data.IsCreated && !_disposed;

        internal AudioFrame(uint sampleRate, uint numChannels, uint samplesPerChannel)
        {
            SampleRate = sampleRate;
            NumChannels = numChannels;
            SamplesPerChannel = samplesPerChannel;
            _disposed = false;

            unsafe
            {
                _data = new NativeArray<byte>((int)(samplesPerChannel * numChannels * sizeof(short)), Allocator.Persistent);
                _dataPtr = (IntPtr)NativeArrayUnsafeUtility.GetUnsafePtr(_data);
            }
        }

        public void Dispose()
        {
            if (!_disposed && _data.IsCreated)
            {
                _data.Dispose();
                _disposed = true;
            }
        }

        public Span<byte> AsSpan()
        {
            if (_disposed)
            {
                Debug.Log("Attempted to access disposed AudioFrame");
                return Span<byte>.Empty;
            }

            unsafe
            {
                return new Span<byte>(_dataPtr.ToPointer(), Length);
            }
        }
    }
}