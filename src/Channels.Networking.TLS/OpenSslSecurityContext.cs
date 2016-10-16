﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Channels.Networking.TLS.Internal.OpenSsl;

namespace Channels.Networking.TLS
{
    public class OpenSslSecurityContext : IDisposable
    {
        internal const int BlockSize = 1024 * 4 - 64; //Current fixed block size

        private bool _initOkay = false;
        private readonly string _hostName;
        private readonly ChannelFactory _channelFactory;
        private readonly bool _isServer;
        private InteropKeys.PK12Certifcate _certifcateInformation;
        private byte[] _alpnSupportedProtocolsBuffer;
        private GCHandle _alpnHandle;
        private IntPtr _sslContext;
        private Interop.alpn_cb _alpnCallback;
        private ApplicationProtocols.ProtocolIds _alpnSupportedProtocols;

        public OpenSslSecurityContext(ChannelFactory channelFactory, string hostName, bool isServer, string pathToPfxFile, string password)
            : this(channelFactory, hostName, isServer, pathToPfxFile, password, 0)
        {
        }

        public unsafe OpenSslSecurityContext(ChannelFactory channelFactory, string hostName, bool isServer, string pathToPfxFile, string password, ApplicationProtocols.ProtocolIds alpnSupportedProtocols)
        {
            if (isServer && string.IsNullOrEmpty(pathToPfxFile))
            {
                throw new ArgumentException("We need a certificate to load if you want to run in server mode");
            }

            InteropCrypto.Init();
            InteropCrypto.OPENSSL_add_all_algorithms_noconf();
            InteropCrypto.CheckForErrorOrThrow(Interop.SSL_library_init());

            _channelFactory = channelFactory;
            _isServer = isServer;
            _alpnSupportedProtocols = alpnSupportedProtocols;

            InteropBio.BioHandle fileBio = new InteropBio.BioHandle();
            try
            {
                fileBio = InteropBio.BIO_new_file_read(pathToPfxFile);
                //Now we pull out the private key, certificate and Authority if they are all there
                _certifcateInformation = new InteropKeys.PK12Certifcate(fileBio, password);
            }
            finally
            {
                fileBio.FreeBio();
            }

            SetupContext();
            SetupAlpn();
        }

        public bool IsServer => _isServer;
        internal InteropKeys.PK12Certifcate CertificateInformation => _certifcateInformation;
        internal IntPtr AplnBuffer => _alpnHandle.AddrOfPinnedObject();
        internal int AplnBufferLength => _alpnSupportedProtocolsBuffer?.Length ?? 0;

        private unsafe void SetupAlpn()
        {
            if (_alpnSupportedProtocols > 0)
            {
                //We need to get a buffer for the ALPN negotiation and pin it for sending to the lower API
                _alpnSupportedProtocolsBuffer = ApplicationProtocols.GetBufferForProtocolId(_alpnSupportedProtocols, false);
                _alpnHandle = GCHandle.Alloc(_alpnSupportedProtocolsBuffer, GCHandleType.Pinned);
                Interop.SSL_CTX_set_alpn_protos(_sslContext, AplnBuffer, (uint)AplnBufferLength);
                if (_isServer)
                {
                    _alpnCallback = alpn_cb;
                    Interop.SSL_CTX_set_alpn_select_cb(_sslContext, _alpnCallback, IntPtr.Zero);
                }
            }
        }

        private unsafe Interop.AlpnStatus alpn_cb(IntPtr ssl, out byte* selProto, out byte selProtoLen, byte* inProtos, int inProtosLen, IntPtr arg)
        {
            byte* matchBuffer = (byte*)_alpnHandle.AddrOfPinnedObject();
            for (int i = 0; i < inProtosLen;)
            {
                var len = inProtos[i];
                var s = new Span<byte>(inProtos + i + 1, len);
                for (int x = 0; x < _alpnSupportedProtocolsBuffer.Length;)
                {
                    var len2 = matchBuffer[x];
                    var sCompare = new Span<byte>(matchBuffer + x + 1, len2);
                    if (sCompare.SequenceEqual(s))
                    {
                        selProto = matchBuffer + x + 1;
                        selProtoLen = len2;
                        return Interop.AlpnStatus.SSL_TLSEXT_ERR_OK;
                    }
                    x += len2 + 1;
                }
                i += len + 1;
            }
            selProto = null;
            selProtoLen = 0;
            return Interop.AlpnStatus.SSL_TLSEXT_ERR_NOACK;
        }

        private void SetupContext()
        {
            if (_isServer)
            {
                _sslContext = Interop.NewServerContext();
            }
            else
            {
                _sslContext = Interop.NewClientContext();
            }
            Interop.SSL_CTX_set_options(_sslContext, Interop.ContextOptions.SSL_OP_NO_SSLv2 | Interop.ContextOptions.SSL_OP_NO_SSLv3);
            Interop.SSL_CTX_set_verify(_sslContext, Interop.VerifyMode.SSL_VERIFY_NONE, IntPtr.Zero);

            if (_certifcateInformation.Handle != IntPtr.Zero)
            {
                if (_certifcateInformation.CertificateHandle != IntPtr.Zero)
                {
                    InteropCrypto.CheckForErrorOrThrow(Interop.SSL_CTX_use_certificate(_sslContext, _certifcateInformation.CertificateHandle));
                }
                if (_certifcateInformation.PrivateKeyHandle != IntPtr.Zero)
                {
                    InteropCrypto.CheckForErrorOrThrow(Interop.SSL_CTX_use_PrivateKey(_sslContext, _certifcateInformation.PrivateKeyHandle));
                }
            }
        }

        public ISecureChannel CreateSecureChannel(IChannel channel)
        {
            var ssl = Interop.SSL_new(_sslContext);
            var chan = new SecureChannel<OpenSslConnectionContext>(channel, _channelFactory, new OpenSslConnectionContext(this, ssl));
            return chan;
        }

        public void Dispose()
        {
            if(_alpnHandle.IsAllocated)
            {
                _alpnHandle.Free();
            }
            _certifcateInformation.Free();
            if(_sslContext != IntPtr.Zero)
            {
                Interop.SSL_CTX_free(_sslContext);
            }
        }
    }
}
