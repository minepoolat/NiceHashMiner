﻿using MinerPlugin;
using MinerPluginToolkitV1;
using Newtonsoft.Json;
using NHM.Common;
using NHM.Common.Enums;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MinerPluginToolkitV1.Configs;

namespace MiniZ
{
    public class MiniZ : MinerBase
    {
        private const double DevFee = 2.0;
        private int _apiPort;
        private string _devices;
        private bool firstStart = true;

        public MiniZ(string uuid) : base(uuid){}

        protected virtual string AlgorithmName(AlgorithmType algorithmType)
        {
            switch (algorithmType)
            {
                case AlgorithmType.ZHash:
                    return "144,5";
                case AlgorithmType.Beam:
                    return "150,5";
                case AlgorithmType.BeamV2:
                    return "150,5,3";
                default:
                    return "";
            }
        }

        public override Tuple<string, string> GetBinAndCwdPaths()
        {
            var pluginRoot = Path.Combine(Paths.MinerPluginsPath(), _uuid);
            var pluginRootBins = Path.Combine(pluginRoot, "bins");
            var binPath = Path.Combine(pluginRootBins, "miniZ.exe");
            var binCwd = pluginRootBins;
            return Tuple.Create(binPath, binCwd);
        }

        public async override Task<ApiData> GetMinerStatsDataAsync()
        {
            var api = new ApiData();

            if (firstStart)
            {
                Thread.Sleep(3000);
                firstStart = false;
                return api;
            }

            try
            {
                JsonApiResponse resp = null;
                using (var client = new TcpClient("127.0.0.1", _apiPort))
                using (var nwStream = client.GetStream())
                {
                    var bytesToSend = Encoding.ASCII.GetBytes("{\"id\":\"0\", \"method\":\"getstat\"}\\n");
                    await nwStream.WriteAsync(bytesToSend, 0, bytesToSend.Length);
                    var bytesToRead = new byte[client.ReceiveBufferSize];
                    var bytesRead = await nwStream.ReadAsync(bytesToRead, 0, client.ReceiveBufferSize);
                    var respStr = Encoding.ASCII.GetString(bytesToRead, 0, bytesRead);
                    resp = JsonConvert.DeserializeObject<JsonApiResponse>(respStr);
                    client.Close();
                }

                // return if we got nothing
                var respOK = resp != null && resp.error == null;
                if (respOK == false) return api;

                var results = resp.result;

                var gpus = _miningPairs.Select(pair => pair.Device);
                var perDeviceSpeedInfo = new Dictionary<string, IReadOnlyList<AlgorithmTypeSpeedPair>>();
                var perDevicePowerInfo = new Dictionary<string, int>();
                var totalSpeed = 0d;
                var totalPowerUsage = 0;
                foreach (var gpu in gpus)
                {
                    var currentDevStats = results.Where(r => r.cudaid == gpu.ID).FirstOrDefault();
                    if (currentDevStats == null) continue;
                    totalSpeed += currentDevStats.speed_sps;
                    perDeviceSpeedInfo.Add(gpu.UUID, new List<AlgorithmTypeSpeedPair>() { new AlgorithmTypeSpeedPair(_algorithmType, currentDevStats.speed_sps * (1 - DevFee * 0.01)) });
                    totalPowerUsage += (int)currentDevStats.gpu_power_usage;
                    perDevicePowerInfo.Add(gpu.UUID, (int)currentDevStats.gpu_power_usage);

                }
                api.AlgorithmSpeedsTotal = new List<AlgorithmTypeSpeedPair> { new AlgorithmTypeSpeedPair(_algorithmType, totalSpeed * (1 - DevFee * 0.01)) };
                api.PowerUsageTotal = totalPowerUsage;
                api.AlgorithmSpeedsPerDevice = perDeviceSpeedInfo;
                api.PowerUsagePerDevice = perDevicePowerInfo;
            }
            catch (Exception e)
            {
                Logger.Error(_logGroup, $"Error occured while getting API stats: {e.Message}");
            }

            return api;
        }

        public async override Task<BenchmarkResult> StartBenchmark(CancellationToken stop, BenchmarkPerformanceType benchmarkType = BenchmarkPerformanceType.Standard)
        {
            // determine benchmark time 
            // settup times
            var benchmarkTime = MinerBenchmarkTimeSettings.ParseBenchmarkTime(new List<int> { 30, 60, 120 }, MinerBenchmarkTimeSettings, _miningPairs, benchmarkType); // in seconds

            var deviceIDs = string.Join("", _miningPairs.Select(pair => $"{pair.Device.DeviceType.ToString()}{pair.Device.ID}"));
            var algoName = AlgorithmName(_algorithmType);
            var logfileName = $"{deviceIDs}_{algoName}_bench.txt";

            var commandLine = CreateCommandLine(DemoUser.BTC) + $" --nocolor --logfile {logfileName}";
            var binPathBinCwdPair = GetBinAndCwdPaths();
            var binPath = binPathBinCwdPair.Item1;
            var binCwd = binPathBinCwdPair.Item2;

            //first delete benchmark file if it exists
            File.Delete(Path.Combine(binCwd, logfileName));

            Logger.Info(_logGroup, $"Benchmarking started with command: {commandLine}");
            var bp = new BenchmarkProcess(binPath, binCwd, commandLine, GetEnvironmentVariables());
            bp.CheckData = (string data) =>
            {
                // we can't read from stdout or stderr, read from logs later
                return new BenchmarkResult();
            };

            var benchmarkTimeout = TimeSpan.FromSeconds(benchmarkTime + 5);
            var benchmarkWait = TimeSpan.FromMilliseconds(1000);
            var t = await MinerToolkit.WaitBenchmarkResult(bp, benchmarkTimeout, benchmarkWait, stop);

            // look for log file and parse that
            try
            {
                var benchHashes = 0d;
                var benchIters = 0;
                var benchHashResult = 0d;
                var targetBenchIters = Math.Max(1, (int)Math.Floor(benchmarkTime / 10d));

                var logFullPath = Path.Combine(binCwd, logfileName);
                var lines = File.ReadLines(logFullPath);
                foreach(var line in lines)
                {
                    var hashrateFoundPair = BenchmarkHelpers.TryGetHashrateAfter(line, "I/s");
                    var hashrate = hashrateFoundPair.Item1;
                    var found = hashrateFoundPair.Item2;

                    if (found)
                    {
                        benchHashes += hashrate;
                        benchIters++;

                        benchHashResult = (benchHashes / benchIters) * (1 - DevFee * 0.01);
                    }
                }

                var success = benchHashResult > 0d;
                return new BenchmarkResult
                {
                    AlgorithmTypeSpeeds = new List<AlgorithmTypeSpeedPair> { new AlgorithmTypeSpeedPair(_algorithmType, benchHashResult) },
                    Success = success
                };
            }
            catch (Exception e)
            {
                Logger.Error(_logGroup, $"Benchmarking failed: {e.Message}");
            }
            return t;
        }

        protected override void Init()
        {
            _devices = string.Join(",", _miningPairs.Select(p => p.Device.ID));
        }

        protected override string MiningCreateCommandLine()
        {
            return CreateCommandLine(_username);
        }

        private string CreateCommandLine(string username)
        {
            // API port function might be blocking
            _apiPort = GetAvaliablePort();

            var algo = AlgorithmName(_algorithmType);

            var urlWithPort = StratumServiceHelpers.GetLocationUrl(_algorithmType, _miningLocation, NhmConectionType.NONE);
            var split = urlWithPort.Split(':');
            var url = split[0];
            var port = split[1];

            var cmd = $"--par={algo} --server={url} --port={port} --user={username} --cuda-devices={_devices} --telemetry={_apiPort} {_extraLaunchParameters}";

            if (_algorithmType == AlgorithmType.ZHash)
            {
                cmd += " --pers=auto";
            }

            return cmd;
        }
    }
}
