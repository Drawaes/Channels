﻿using System;
using System.Threading;

namespace Channels
{
    /// <summary>
    /// Leased buffer from the <see cref="IBufferPool"/>
    /// </summary>
    public struct PooledBuffer
    {
        private IBufferPool _pool;
        private object _buffer;
        private int _refCount;

        /// <summary>
        /// Creates a <see cref="PooledBuffer"/> with the specified <see cref="IBufferPool"/> and trackingObject
        /// </summary>
        /// <param name="pool">The buffer pool associated with this <see cref="PooledBuffer"/></param>
        /// <param name="buffer">A tracking object used by the <see cref="IBufferPool"/> to track the <see cref="PooledBuffer"/></param>
        public PooledBuffer(IBufferPool pool, object buffer)
        {
            _pool = pool;
            _buffer = buffer;
            _refCount = 1;
        }

        /// <summary>
        /// The underlying data exposed from the <see cref="IBufferPool"/>.
        /// </summary>
        public Span<byte> Data => _buffer == null ? Span<byte>.Empty : _pool.GetBuffer(_buffer);

        // Keep these internal for now since nobody needs to use these but the channels system
        internal void AddReference()
        {
            Interlocked.Increment(ref _refCount);
        }

        internal void RemoveReference()
        {
            if (Interlocked.Decrement(ref _refCount) == 0)
            {
                _pool.Return(_buffer);
            }
        }
    }
}
