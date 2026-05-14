using NetMQ;
using NetMQ.Sockets;
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace QuantConnect.Algorithm.CSharp.Core.Pricing
{
    /// <summary>
    /// Synchronous REQ/REP ZeroMQ client for Julia pricing functions.
    /// One instance per process — not thread-safe; callers must not share across threads.
    ///
    /// Wire format (all multi-byte values little-endian):
    ///   1  byte   cmd
    ///   1  byte   is_call        (0x01 = call, 0x00 = put)
    ///   8  bytes  S              Float64
    ///   8  bytes  K              Float64
    ///   8  bytes  T              Float64  (years to expiry)
    ///   8  bytes  sigma          Float64  (ignored for CMD_IV)
    ///   8  bytes  price          Float64  (market price, used only by CMD_IV)
    ///   8  bytes  calc_time_utc  Int64    (Unix timestamp seconds, UTC)
    ///   4  bytes  len_ticker     Int32
    ///   …         ticker         UTF-8 bytes
    ///   4  bytes  len_yc         Int32
    ///   …         yield_curve    UTF-8 bytes
    ///   8  bytes  d_spot         Float64  (0.0 → server default 0.1)
    ///   8  bytes  d_sigma        Float64  (0.0 → server default 0.001)
    ///   8  bytes  d_time         Float64  (0.0 → server default 1/365)
    ///   4  bytes  time_steps     Int32    (0 → server default 200)
    ///   4  bytes  space_steps    Int32    (0 → server default 200)
    ///
    /// Reply (success):  4 bytes Float32 LE
    /// Reply (error):    0xFF byte + UTF-8 error message
    /// </summary>
    public sealed class JuliaPricingClient : IDisposable
    {
        private readonly RequestSocket _socket;
        private static readonly Action<string> _log = Console.WriteLine;
        private readonly object _lock = new object();

        public enum Command : byte
        {
            Price      = 0x00,
            IV         = 0x01,
            Delta      = 0x02,
            Vega       = 0x03,
            Theta      = 0x04,
            Gamma      = 0x05,
            Speed      = 0x06,
            GammaDecay = 0x07,
            GammaVol   = 0x08,
            ThetaDecay = 0x09,
            VegaDecay  = 0x0A,
            Vanna      = 0x0B,
            Volga      = 0x0C,
            MnyFwd     = 0x0D
        }

        public JuliaPricingClient(string endpoint = "tcp://127.0.0.1:5555")
        // public JuliaPricingClient(string endpoint = "ipc:///tmp/julia_pricer")
        {
            _socket = new RequestSocket();
            _socket.Connect(endpoint);
        }

        /// <summary>
        /// Builds and sends the request frame, then reads and validates the reply.
        /// </summary>
        private double Send(
            Command command,
            bool isCall,
            double s,
            double k,
            double t,
            double sigma,
            double price,
            long calcTimeUtc,
            string ticker,
            string yieldCurve = "YC",
            double dSpot = 0.0,
            double dSigma = 0.0,
            double dTime = 0.0,
            int timeSteps = 0,
            int spaceSteps = 0
            )
        {
            byte[] tickerBytes = Encoding.UTF8.GetBytes(ticker);
            byte[] yieldCurveBytes = Encoding.UTF8.GetBytes(yieldCurve);

            int byteCount = 1 // cmd
                + 1 // is_call
                + 6 * sizeof(double) // s, k, t, sigma, price, calc_time (as doubles before int64)
                + sizeof(long) // calc_time_utc  (Int64)
                + sizeof(int) + tickerBytes.Length
                + sizeof(int) + yieldCurveBytes.Length
                + 3 * sizeof(double) // d_spot, d_sigma, d_time
                + 2 * sizeof(int); // time_steps, space_steps

            // Recalculate precisely: cmd(1) + is_call(1) + S(8)+K(8)+T(8)+sigma(8)+price(8) + calc_time_utc(8)
            //                      + len_ticker(4)+ticker + len_yc(4)+yc
            //                      + d_spot(8)+d_sigma(8)+d_time(8) + time_steps(4)+space_steps(4)
            byteCount = 1 + 1
                + 5 * sizeof(double) // s, k, t, sigma, price
                + sizeof(long) // calc_time_utc
                + sizeof(int) + tickerBytes.Length
                + sizeof(int) + yieldCurveBytes.Length
                + 3 * sizeof(double) // d_spot, d_sigma, d_time
                + 2 * sizeof(int); // time_steps, space_steps

            byte[] buf = new byte[byteCount];
            int pos = 0;

            buf[pos++] = (byte)command;
            buf[pos++] = isCall ? (byte)0x01 : (byte)0x00;

            void WriteDouble(double v)
            {
                MemoryMarshal.Write(buf.AsSpan(pos), ref v);
                pos += sizeof(double);
            }

            void WriteLong(long v)
            {
                MemoryMarshal.Write(buf.AsSpan(pos), ref v);
                pos += sizeof(long);
            }

            void WriteInt(int v)
            {
                MemoryMarshal.Write(buf.AsSpan(pos), ref v);
                pos += sizeof(int);
            }

            void WriteBytes(byte[] b)
            {
                WriteInt(b.Length);
                b.CopyTo(buf, pos);
                pos += b.Length;
            }

            WriteDouble(s);
            WriteDouble(k);
            WriteDouble(t);
            WriteDouble(sigma);
            WriteDouble(price);
            WriteLong(calcTimeUtc);
            WriteBytes(tickerBytes);
            WriteBytes(yieldCurveBytes);
            WriteDouble(dSpot);
            WriteDouble(dSigma);
            WriteDouble(dTime);
            WriteInt(timeSteps);
            WriteInt(spaceSteps);

            lock (_lock)
            {
                _socket.SendFrame(buf);

                byte[] reply = _socket.ReceiveFrameBytes();
                // sw.Stop();
                // _log($"[JuliaPricing] {command} | {sw.ElapsedMilliseconds} ms");
                // IV takes 25-120 ms

                if (reply.Length == 0)
                {
                    throw new InvalidOperationException("Julia pricing: empty reply");
                }

                byte type = reply[0];
                ReadOnlySpan<byte> payload = reply.AsSpan(1);

                switch (type)
                {
                    case 0x02:
                    {
                        return MemoryMarshal.Read<float>(payload);
                    }
                    case 0x03:
                    {
                        string errorMessage = reply.Length > 1
                            ? Encoding.UTF8.GetString(reply, 1, reply.Length - 1)
                            : "(no message)";
                        throw new InvalidOperationException($"Julia pricing error: {errorMessage}");
                    }
                    default:
                        throw new InvalidOperationException($"Julia pricing: unexpected type {type}");
                }
            }
        }

        // ── Core greeks ──────────────────────────────────────────────────────────

        public double Price(bool isCall, double s, double k, double t, double sigma,
            double price, long calcTimeUtc, string ticker, string yieldCurve = "YC",
            double dSpot = 0.0, double dSigma = 0.0, double dTime = 0.0,
            int timeSteps = 0, int spaceSteps = 0)
            => double.IsNaN(sigma) ? double.NaN : Send(Command.Price, isCall, s, k, t, sigma, price, calcTimeUtc, ticker, yieldCurve,
                    dSpot, dSigma, dTime, timeSteps, spaceSteps);

        /// <param name="price">Market price of the option (used to back out IV).</param>
        public double IV(bool isCall, double s, double k, double t,
            double price, long calcTimeUtc, string ticker, string yieldCurve = "YC",
            double dSpot = 0.0, double dSigma = 0.0, double dTime = 0.0,
            int timeSteps = 0, int spaceSteps = 0)
            => double.IsNaN(price) ? double.NaN : Send(Command.IV, isCall, s, k, t, sigma: 0.0, price, calcTimeUtc, ticker, yieldCurve,
                    dSpot, dSigma, dTime, timeSteps, spaceSteps);

        public double Delta(bool isCall, double s, double k, double t, double sigma,
            double price, long calcTimeUtc, string ticker, string yieldCurve = "YC",
            double dSpot = 0.0, double dSigma = 0.0, double dTime = 0.0,
            int timeSteps = 0, int spaceSteps = 0)
            => double.IsNaN(sigma) ? double.NaN : Send(Command.Delta, isCall, s, k, t, sigma, price, calcTimeUtc, ticker, yieldCurve,
                    dSpot, dSigma, dTime, timeSteps, spaceSteps);

        public double Vega(bool isCall, double s, double k, double t, double sigma,
            double price, long calcTimeUtc, string ticker, string yieldCurve = "YC",
            double dSpot = 0.0, double dSigma = 0.0, double dTime = 0.0,
            int timeSteps = 0, int spaceSteps = 0)
            => double.IsNaN(sigma) ? double.NaN : Send(Command.Vega, isCall, s, k, t, sigma, price, calcTimeUtc, ticker, yieldCurve,
                    dSpot, dSigma, dTime, timeSteps, spaceSteps);

        public double Theta(bool isCall, double s, double k, double t, double sigma,
            double price, long calcTimeUtc, string ticker, string yieldCurve = "YC",
            double dSpot = 0.0, double dSigma = 0.0, double dTime = 0.0,
            int timeSteps = 0, int spaceSteps = 0)
            => double.IsNaN(sigma) ? double.NaN : Send(Command.Theta, isCall, s, k, t, sigma, price, calcTimeUtc, ticker, yieldCurve,
                    dSpot, dSigma, dTime, timeSteps, spaceSteps);

        public double Gamma(bool isCall, double s, double k, double t, double sigma,
            double price, long calcTimeUtc, string ticker, string yieldCurve = "YC",
            double dSpot = 0.0, double dSigma = 0.0, double dTime = 0.0,
            int timeSteps = 0, int spaceSteps = 0)
            => double.IsNaN(sigma) ? double.NaN : Send(Command.Gamma, isCall, s, k, t, sigma, price, calcTimeUtc, ticker, yieldCurve,
                    dSpot, dSigma, dTime, timeSteps, spaceSteps);

        // ── Higher-order greeks ───────────────────────────────────────────────────

        /// <summary>dGamma/dS — third derivative of price w.r.t. spot.</summary>
        public double Speed(bool isCall, double s, double k, double t, double sigma,
            double price, long calcTimeUtc, string ticker, string yieldCurve = "YC",
            double dSpot = 0.0, double dSigma = 0.0, double dTime = 0.0,
            int timeSteps = 0, int spaceSteps = 0)
            => double.IsNaN(sigma) ? double.NaN : Send(Command.Speed, isCall, s, k, t, sigma, price, calcTimeUtc, ticker, yieldCurve,
                    dSpot, dSigma, dTime, timeSteps, spaceSteps);

        /// <summary>dGamma/dt — decay of gamma over time.</summary>
        public double GammaDecay(bool isCall, double s, double k, double t, double sigma,
            double price, long calcTimeUtc, string ticker, string yieldCurve = "YC",
            double dSpot = 0.0, double dSigma = 0.0, double dTime = 0.0,
            int timeSteps = 0, int spaceSteps = 0)
            => double.IsNaN(sigma) ? double.NaN : Send(Command.GammaDecay, isCall, s, k, t, sigma, price, calcTimeUtc, ticker, yieldCurve,
                    dSpot, dSigma, dTime, timeSteps, spaceSteps);

        /// <summary>dGamma/dSigma — sensitivity of gamma to volatility.</summary>
        public double GammaVol(bool isCall, double s, double k, double t, double sigma,
            double price, long calcTimeUtc, string ticker, string yieldCurve = "YC",
            double dSpot = 0.0, double dSigma = 0.0, double dTime = 0.0,
            int timeSteps = 0, int spaceSteps = 0)
            => double.IsNaN(sigma) ? double.NaN : Send(Command.GammaVol, isCall, s, k, t, sigma, price, calcTimeUtc, ticker, yieldCurve,
                    dSpot, dSigma, dTime, timeSteps, spaceSteps);

        /// <summary>dTheta/dt — decay of theta over time.</summary>
        public double ThetaDecay(bool isCall, double s, double k, double t, double sigma,
            double price, long calcTimeUtc, string ticker, string yieldCurve = "YC",
            double dSpot = 0.0, double dSigma = 0.0, double dTime = 0.0,
            int timeSteps = 0, int spaceSteps = 0)
            => double.IsNaN(sigma) ? double.NaN : Send(Command.ThetaDecay, isCall, s, k, t, sigma, price, calcTimeUtc, ticker, yieldCurve,
                    dSpot, dSigma, dTime, timeSteps, spaceSteps);

        /// <summary>dVega/dt — decay of vega over time (Veta).</summary>
        public double VegaDecay(bool isCall, double s, double k, double t, double sigma,
            double price, long calcTimeUtc, string ticker, string yieldCurve = "YC",
            double dSpot = 0.0, double dSigma = 0.0, double dTime = 0.0,
            int timeSteps = 0, int spaceSteps = 0)
            => double.IsNaN(sigma) ? double.NaN : Send(Command.VegaDecay, isCall, s, k, t, sigma, price, calcTimeUtc, ticker, yieldCurve,
                    dSpot, dSigma, dTime, timeSteps, spaceSteps);

        /// <summary>dDelta/dSigma — sensitivity of delta to volatility.</summary>
        public double Vanna(bool isCall, double s, double k, double t, double sigma,
            double price, long calcTimeUtc, string ticker, string yieldCurve = "YC",
            double dSpot = 0.0, double dSigma = 0.0, double dTime = 0.0,
            int timeSteps = 0, int spaceSteps = 0)
            => double.IsNaN(sigma) ? double.NaN : Send(Command.Vanna, isCall, s, k, t, sigma, price, calcTimeUtc, ticker, yieldCurve,
                    dSpot, dSigma, dTime, timeSteps, spaceSteps);

        /// <summary>dVega/dSigma — sensitivity of vega to volatility (Vomma).</summary>
        public double Volga(bool isCall, double s, double k, double t, double sigma,
            double price, long calcTimeUtc, string ticker, string yieldCurve = "YC",
            double dSpot = 0.0, double dSigma = 0.0, double dTime = 0.0,
            int timeSteps = 0, int spaceSteps = 0)
            => double.IsNaN(sigma) ? double.NaN : Send(Command.Volga, isCall, s, k, t, sigma, price, calcTimeUtc, ticker, yieldCurve,
                    dSpot, dSigma, dTime, timeSteps, spaceSteps);
        
        
        /// <summary>Moneyness forward.</summary>
        public double MnyFwd(double s, double k, double t, long calcTimeUtc, string ticker, string yieldCurve = "YC")
            => Send(Command.MnyFwd, false, s, k, t, 0, 0, calcTimeUtc, ticker, yieldCurve);
        
        

        public void Dispose() => _socket.Dispose();
    }
}
