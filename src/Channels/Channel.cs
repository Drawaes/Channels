// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Channels
{
    /// <summary>
    /// Default <see cref="IWritableChannel"/> and <see cref="IReadableChannel"/> implementation.
    /// </summary>
    public class Channel : IReadableChannel, IWritableChannel
    {
        private static readonly Action _awaitableIsCompleted = () => { };
        private static readonly Action _awaitableIsNotCompleted = () => { };

        private static Task _completedTask = Task.FromResult(0);

        internal IBufferPool BufferPool { get; }
        internal ISegmentPool SegmentPool { get; }

        private Action _awaitableState;

        private PooledBufferSegment _head;
        private PooledBufferSegment _tail;

        private int _consumingState;
        private int _producingState;
        private object _sync = new object();

        // REVIEW: This object might be getting a little big :)
        private readonly TaskCompletionSource<object> _readingTcs = new TaskCompletionSource<object>();
        private readonly TaskCompletionSource<object> _writingTcs = new TaskCompletionSource<object>();
        private readonly TaskCompletionSource<object> _startingReadingTcs = new TaskCompletionSource<object>();

        /// <summary>
        /// Initializes the <see cref="Channel"/> with the specifed <see cref="IBufferPool"/>.
        /// </summary>
        /// <param name="pool"></param>
        public Channel(IBufferPool pool) : this(pool, new SegmentPool())
        {
        }

        /// <summary>
        /// Initializes the <see cref="Channel"/> with the specifed <see cref="IBufferPool"/>.
        /// </summary>
        /// <param name="pool"></param>
        /// <param name="segmentPool"></param>
        internal Channel(IBufferPool pool, ISegmentPool segmentPool)
        {
            BufferPool = pool;
            SegmentPool = segmentPool;
            _awaitableState = _awaitableIsNotCompleted;
        }

        /// <summary>
        /// A <see cref="Task"/> that completes when the consumer starts consuming the <see cref="IReadableChannel"/>.
        /// </summary>
        public Task ReadingStarted => _startingReadingTcs.Task;

        /// <summary>
        /// Gets a task that completes when the channel is completed and has no more data to be read.
        /// </summary>
        public Task WriterCompleted => _readingTcs.Task;

        /// <summary>
        /// Gets a task that completes when the consumer is completed reading.
        /// </summary>
        /// <remarks>When this task is triggered, the producer should stop producing data.</remarks>
        public Task ReaderCompleted => _writingTcs.Task;

        Task IReadableChannel.Completion => _readingTcs.Task;

        Task IWritableChannel.Completion => _writingTcs.Task;

        internal bool IsCompleted => ReferenceEquals(_awaitableState, _awaitableIsCompleted);

        /// <summary>
        /// Allocates memory from the channel to write into.
        /// </summary>
        /// <param name="minimumSize">The minimum size buffer to allocate</param>
        /// <returns>A <see cref="WritableBuffer"/> that can be written to.</returns>
        public WritableBuffer Alloc(int minimumSize = 0)
        {
            // TODO: Make this configurable for channel creation
            const int bufferSize = 4096;

            if (Interlocked.CompareExchange(ref _producingState, 1, 0) != 0)
            {
                throw new InvalidOperationException("Already producing.");
            }

            PooledBufferSegment segment = null;

            if (_tail != null && !_tail.ReadOnly)
            {
                // Try to return the tail so the calling code can append to it
                int remaining = _tail.Buffer.Data.Length - _tail.End;

                if (minimumSize <= remaining)
                {
                    segment = _tail;
                }
            }

            if (segment == null && minimumSize > 0)
            {
                // We're out of tail space so lease a new segment only if the requested size > 0
                segment = SegmentPool.Lease(BufferPool.Lease(bufferSize));
            }

            lock (_sync)
            {
                if (_head == null)
                {
                    _head = segment;
                }
                else if (segment != null && segment != _tail)
                {
                    // Append the segment to the tail if it's non-null
                    _tail.Next = segment;
                    _tail = segment;
                }

                return new WritableBuffer(this, segment, bufferSize);
            }
        }

        internal void Append(WritableBuffer buffer)
        {
            lock (_sync)
            {
                if (Interlocked.CompareExchange(ref _producingState, 0, 1) != 1)
                {
                    throw new InvalidOperationException("No ongoing producing operation to complete.");
                }

                if (buffer.IsDefault)
                {
                    // REVIEW: Should we signal the completion?
                    return;
                }

                if (_head == null)
                {
                    // Update the head to point to the head of the buffer. This
                    // happens if we called alloc(0) then write
                    _head = buffer.Head;
                    _head.Start = buffer.HeadIndex;
                }
                // If buffer.Head == tail it means we appended data to the tail
                else if (_tail != null && buffer.Head != _tail)
                {
                    // If we have a tail point next to the head of the buffer
                    _tail.Next = buffer.Head;
                }

                // Always update tail to the buffer's tail
                _tail = buffer.Tail;
                _tail.End = buffer.TailIndex;
            }
        }

        internal Task CompleteWriteAsync()
        {
            lock (_sync)
            {
                Complete();

                // Apply back pressure here
                return _completedTask;
            }
        }

        private void Complete()
        {
            var awaitableState = Interlocked.Exchange(
                ref _awaitableState,
                _awaitableIsCompleted);

            if (!ReferenceEquals(awaitableState, _awaitableIsCompleted) &&
                !ReferenceEquals(awaitableState, _awaitableIsNotCompleted))
            {
                awaitableState();
            }
        }

        private ReadableBuffer Read()
        {
            if (Interlocked.CompareExchange(ref _consumingState, 1, 0) != 0)
            {
                throw new InvalidOperationException("Already consuming.");
            }

            return new ReadableBuffer(this, new ReadCursor(_head), new ReadCursor(_tail, _tail?.End ?? 0));
        }

        internal void EndRead(ReadCursor end)
        {
            EndRead(end, end);
        }

        internal void EndRead(
            ReadCursor consumed,
            ReadCursor examined)
        {
            PooledBufferSegment returnStart = null;
            PooledBufferSegment returnEnd = null;

            lock (_sync)
            {
                if (!consumed.IsDefault)
                {
                    returnStart = _head;
                    returnEnd = consumed.Segment;
                    _head = consumed.Segment;
                    _head.Start = consumed.Index;
                }

                if (!examined.IsDefault &&
                    examined.IsEnd &&
                    WriterCompleted.Status == TaskStatus.WaitingForActivation)
                {
                    Interlocked.CompareExchange(
                        ref _awaitableState,
                        _awaitableIsNotCompleted,
                        _awaitableIsCompleted);
                }
            }

            while (returnStart != returnEnd)
            {
                var returnSegment = returnStart;
                returnStart = returnStart.Next;
                returnSegment.Dispose();
            }

            if (Interlocked.CompareExchange(ref _consumingState, 0, 1) != 1)
            {
                throw new InvalidOperationException("No ongoing consuming operation to complete.");
            }
        }

        /// <summary>
        /// Marks the channel as being complete, meaning no more items will be written to it.
        /// </summary>
        /// <param name="exception">Optional Exception indicating a failure that's causing the channel to complete.</param>
        public void CompleteWriting(Exception exception = null)
        {
            lock (_sync)
            {
                if (exception != null)
                {
                    _readingTcs.TrySetException(exception);
                }
                else
                {
                    _readingTcs.TrySetResult(null);
                }

                Complete();

                if (_writingTcs.Task.IsCompleted)
                {
                    Dispose();
                }
            }
        }

        /// <summary>
        /// Signal to the producer that the consumer is done reading.
        /// </summary>
        /// <param name="exception">Optional Exception indicating a failure that's causing the channel to complete.</param>
        public void CompleteReading(Exception exception = null)
        {
            lock (_sync)
            {
                if (exception != null)
                {
                    _writingTcs.TrySetException(exception);
                }
                else
                {
                    _writingTcs.TrySetResult(null);
                }

                if (_readingTcs.Task.IsCompleted)
                {
                    Dispose();
                }
            }
        }

        /// <summary>
        /// Asynchronously reads a sequence of bytes from the current <see cref="IReadableChannel"/>.
        /// </summary>
        /// <returns>A <see cref="ChannelAwaitable"/> representing the asynchronous read operation.</returns>
        public ChannelAwaitable ReadAsync() => new ChannelAwaitable(this);

        internal void OnCompleted(Action continuation)
        {
            _startingReadingTcs.TrySetResult(null);

            var awaitableState = Interlocked.CompareExchange(
                ref _awaitableState,
                continuation,
                _awaitableIsNotCompleted);

            if (ReferenceEquals(awaitableState, _awaitableIsNotCompleted))
            {
                return;
            }
            else if (ReferenceEquals(awaitableState, _awaitableIsCompleted))
            {
                // Dispatch here to avoid stack diving
                // Task.Run(continuation);
                continuation();
            }
            else
            {
                _readingTcs.SetException(new InvalidOperationException("Concurrent reads are not supported."));

                Interlocked.Exchange(
                    ref _awaitableState,
                    _awaitableIsCompleted);

                Task.Run(continuation);
                Task.Run(awaitableState);
            }
        }

        internal void UnsafeOnCompleted(Action continuation)
        {
            OnCompleted(continuation);
        }

        internal ReadableBuffer GetResult()
        {
            if (!IsCompleted)
            {
                throw new InvalidOperationException("can't GetResult unless completed");
            }

            if (_readingTcs.Task.IsCompleted)
            {
                // Observe any exceptions if the reading task is completed
                _readingTcs.Task.GetAwaiter().GetResult();
            }

            return Read();
        }

        private void Dispose()
        {
            Debug.Assert(_writingTcs.Task.IsCompleted, "Not completed writing");
            Debug.Assert(_readingTcs.Task.IsCompleted, "Not completed reading");

            lock (_sync)
            {
                // Return all segments
                var segment = _head;
                while (segment != null)
                {
                    var returnSegment = segment;
                    segment = segment.Next;

                    returnSegment.Dispose();
                    SegmentPool.Return(returnSegment);
                }

                _head = null;
                _tail = null;
            }
        }
    }
}
