using System;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using WSLCManager.Helpers;
using WSLCManager.Models;
using WSLCManager.Services;

Console.OutputEncoding = Encoding.UTF8;

var sb = new StringBuilder();
void W(string s)
{
    sb.AppendLine(s);
    Console.WriteLine(s);
}

void Sec(string t) { W(""); W("══════ " + t + " ══════"); }

var outFile = Path.Combine(AppContext.BaseDirectory, "probe_result.txt");

try
{
    Sec("1. 可执行文件定位");
    W("wsl.exe  = " + (ProcRunner.ResolveExecutable("wsl.exe") ?? "(未找到)"));
    W("wslc.exe = " + (ProcRunner.ResolveExecutable("wslc.exe") ?? "(未找到)"));

    Sec("2. 环境检测");
    var st = InstallService.Detect();
    W($"WSL 是否安装 : {st.WslFound}");
    W($"WSL 版本     : {st.WslVersion}");
    W($"解析版本号   : {st.WslVersionParsed}");
    W($"内核版本     : {st.KernelVersion}");
    W($"默认发行版   : {st.DefaultDistro}");
    W($"支持 wslc    : {st.SupportsWslc}  (最低要求 {WslEnvStatus.MinWslForWslc})");
    W($"wslc 是否安装: {st.WslcFound}  path={st.WslcPath}");
    W($"wslc 版本    : {st.WslcVersion}");
    W($"摘要         : {st.Summary}");
    W("");
    W("--- wsl --version 原文 ---");
    W(st.RawWslVersion);

    if (st.WslcFound)
    {
        Sec("3. wslc --help 原文");
        W(st.RawWslcHelp);

        Sec("4. 容器列表（原始输出）");
        var (items, raw) = await WslcService.ListContainersAsync(true);
        W($"exit={raw.ExitCode} success={raw.Success}");
        W("--- 原始 ---");
        W(raw.All);
        W($"--- 解析出 {items.Count} 条 ---");
        foreach (var c in items)
            W($"  [{c.StateText}] name={c.Name} image={c.Image} status={c.Status} ports={c.Ports} id={c.ShortId}");

        Sec("5. 镜像列表（原始输出）");
        var (imgs, iraw) = await WslcService.ListImagesAsync();
        W($"exit={iraw.ExitCode} success={iraw.Success}");
        W("--- 原始 ---");
        W(iraw.All);
        W($"--- 解析出 {imgs.Count} 条 ---");
        foreach (var i in imgs)
            W($"  ref={i.Reference} id={i.ShortId} size={i.Size} created={i.Created}");

        Sec("6. 性能采集（原始输出）");
        var (stats, sraw) = await WslcService.StatsOnceAsync();
        W($"exit={sraw.ExitCode} success={sraw.Success} err={sraw.ErrorMessage}");
        W("--- 原始 ---");
        W(sraw.All);
        W($"--- 解析出 {stats.Count} 条 ---");
        foreach (var s in stats)
            W($"  name={s.Name} cpu={s.Cpu}({s.CpuValue:F1}) mem={s.Mem} mem%={s.MemPercent}({s.MemValue:F1}) net={s.Net} block={s.Block} pids={s.Pids}");
    }
    else
    {
        Sec("3~6 跳过");
        W("wslc 未安装，跳过 CLI 解析验证。");
    }

    Sec("7. 镜像检索（本地瞬时 + 远程限时）");
    var swAll = System.Diagnostics.Stopwatch.StartNew();
    foreach (var q in new[] { "nginx", "redis", "数据库", "向量", "消息队列", "ai", "zzzznotexist" })
    {
        var t0 = swAll.ElapsedMilliseconds;

        // ① 本地（应当瞬时，微秒级）
        var local = ImageSearchService.SearchLocal(q);
        var tLocal = swAll.ElapsedMilliseconds - t0;

        // ② 远程（限时 3s）
        var t1 = swAll.ElapsedMilliseconds;
        var remote = await ImageSearchService.SearchRemoteAsync(q);
        var tRemote = swAll.ElapsedMilliseconds - t1;

        W($"query={q}  本地={local.Items.Count}条/{tLocal}ms  远程={remote.Count}条/{tRemote}ms");
        foreach (var it in local.Items.Take(4))
            W($"   - {it.PullName}   [{it.SourceText}] {it.Description}");
    }
    W($"检索总耗时 {swAll.ElapsedMilliseconds}ms（7 个词，其中远程每次最多 3000ms）");

    Sec("8. ContainerSpec 参数生成自检");
    var spec = new ContainerSpec
    {
        Image = "nginx:latest",
        Name = "web",
        Detach = true,
        AutoRemove = true,
        RestartPolicy = "unless-stopped",
        Command = "bash -c 'echo hello world'"
    };
    spec.Ports.Add(new ContainerSpec.PortMap { HostPort = "8080", ContainerPort = "80" });
    spec.Volumes.Add(new ContainerSpec.VolumeMap { HostPath = @"D:\data", ContainerPath = "/data", ReadOnly = true });
    spec.Env.Add(new ContainerSpec.EnvVar { Key = "TZ", Value = "Asia/Shanghai" });
    W("命令行: " + spec.PreviewCommandLine());
    W("参数个数: " + spec.ToArgs().Count);
    foreach (var a in spec.ToArgs()) W("   " + a);

    Sec("9. 拉取目标拼接自检（加速前缀 → 实际交给 wslc 的名字）");
    int sec9pass = 0, sec9fail = 0;
    foreach (var (img, mirror, wantPrimary) in new (string, string, string)[]
             {
                 ("alpine:latest", "https://mirror.example.com", "mirror.example.com/library/alpine:latest"),
                 ("nginx:1.27",    "mirror.example.com",         "mirror.example.com/library/nginx:1.27"),
                 ("redis",         "https://mirror.example.com/", "mirror.example.com/library/redis"),
                 ("jc21/nginx-proxy-manager:latest", "mirror.example.com",
                                                            "mirror.example.com/jc21/nginx-proxy-manager:latest"),
                 ("mirror.example.com/alpine:latest", "https://mirror.example.com",
                                                            "mirror.example.com/alpine:latest"),
                 ("alpine:latest", "",                       "alpine:latest"),
             })
    {
        var t = WslcService.ComposePullTarget(img, mirror);
        var ok = string.Equals(t, wantPrimary, StringComparison.Ordinal);
        if (ok) sec9pass++; else sec9fail++;
        var cands = string.Join(" , ", WslcService.PullCandidates(img, mirror));
        W($"镜像=\"{img}\"  前缀=\"{mirror}\"");
        W($"    主目标 = {t}    {(ok ? "PASS" : "!! FAIL 期望 " + wantPrimary)}");
        W($"    候选序列 = [ {cands} ]");
    }
    // 关键点：官方镜像必须补 library/ —— 实测 mirror2.example.com 对裸名直接回 denied；
    // 同时检测路径必须与拉取路径同源（TestMirrorAsync 现在直接调 ComposePullTarget）。
    W($"  断言 官方镜像补 library/      : {(WslcService.ComposePullTarget("alpine:latest", "h").EndsWith("/library/alpine:latest") ? "PASS" : "!! FAIL")}");
    W($"  断言 非官方镜像不补           : {(WslcService.ComposePullTarget("jc21/x:1", "h") == "h/jc21/x:1" ? "PASS" : "!! FAIL")}");
    // 注意：registry 判定要求首段含 '.' 或 ':' 或是 localhost；
    // 「myuser/nginx:1」这种单段无点的是 Docker Hub 用户命名空间，**应当**被加速。
    W($"  断言 已带 registry 不重复加速 : {(WslcService.ComposePullTarget("mirror.example.com/nginx:1", "other.io") == "mirror.example.com/nginx:1" ? "PASS" : "!! FAIL")}");
    W($"  断言 用户命名空间会被加速   : {(WslcService.ComposePullTarget("myuser/nginx:1", "h.io") == "h.io/myuser/nginx:1" ? "PASS" : "!! FAIL")}");
    W($"  断言 官方镜像有二级候选(裸名) : {(WslcService.PullCandidates("alpine:latest", "h").Count == 2 ? "PASS" : "!! FAIL")}");
    W($"  第 9 节小结: {sec9pass} PASS / {sec9fail} FAIL");

    Sec("10. 容器网络信息解析（wslc inspect → IP / 网关）");
    // ① 离线夹具：`wslc inspect -f json` 的真实形态（单行 JSON 数组）
    const string fixture =
        "[{\"HostConfig\":{\"NetworkMode\":\"bridge\"},\"Name\":\"/fierce_urals\"," +
        "\"NetworkSettings\":{\"Networks\":{\"bridge\":{\"Gateway\":\"172.17.0.1\"," +
        "\"IPAddress\":\"172.17.0.2\",\"IPPrefixLen\":16,\"MacAddress\":\"02:42:ac:11:00:02\"}}}," +
        "\"Ports\":{\"80/tcp\":[{\"HostIp\":\"127.0.0.1\",\"HostPort\":\"80\"}]}," +
        "\"State\":{\"Status\":\"running\"}}]";

    var fx = WslcService.ParseNetInfo(fixture);
    W($"夹具(单行) 网络={fx.NetworkName} IP={fx.Ip} 网关={fx.Gateway} 端口={fx.PortsText} Ok={fx.Ok}");
    W($"  夹具长度={fixture.Length}  解析错误=\"{fx.ParseError}\"");
    W($"  断言 IP=172.17.0.2  : {(fx.Ip == "172.17.0.2" ? "PASS" : "!! FAIL")}");
    W($"  断言 网关=172.17.0.1 : {(fx.Gateway == "172.17.0.1" ? "PASS" : "!! FAIL")}");
    W($"  断言 网络=bridge     : {(fx.NetworkName == "bridge" ? "PASS" : "!! FAIL")}");
    W($"  断言 端口文本         : {(fx.PortsText.Contains("80/tcp") ? "PASS" : "!! FAIL")}");

    // ② 缩进 JSON（不带 -f 的形态）同样必须能解析
    var fxIndent = WslcService.ParseNetInfo(fixture.Replace("\",\"", "\",\n  \""));
    W($"夹具(多行) IP={fxIndent.Ip} : {(fxIndent.Ip == "172.17.0.2" ? "PASS" : "!! FAIL")}");

    // ③ 垃圾输入不能抛异常（wslc 报错时会把错误文本丢进 stdout）
    var fxBad = WslcService.ParseNetInfo("wslc: 错误：找不到容器");
    W($"夹具(垃圾输入) Ok={fxBad.Ok} IP=\"{fxBad.Ip}\" : {(!fxBad.Ok && fxBad.Ip.Length == 0 ? "PASS" : "!! FAIL")}");

    Sec("11. 终端 shell 探测（实测运行中的容器）");
    var (allC, _) = await WslcService.ListContainersAsync(true);
    var runC = allC.FirstOrDefault(x => x.IsRunning);
    if (runC == null)
    {
        W("没有运行中的容器，跳过实测。");
    }
    else
    {
        var shells = await WslcService.DetectShellsAsync(runC.Name);
        var text = shells.Count == 0 ? "(无)" : string.Join(", ", shells);
        W($"容器 {runC.Name} 探测到 shell: {text}");
        W($"  断言 探测结果非空        : {(shells.Count > 0 ? "PASS" : "!! FAIL（该镜像可能确实没有 shell）")}");
        W($"  断言 bash 优先于 sh      : {(shells.IndexOf("bash") < shells.IndexOf("sh") || shells.IndexOf("sh") < 0 ? "PASS" : "!! FAIL")}");

        // 诊断：把底层 exec 调用的原始返回打出来（探测失败时用来看是路径错还是容器没有 shell）
        var rawExec = ProcRunner.Run(WslcService.ExePath,
            new[] { "exec", runC.Name, "sh", "-c", "for s in bash sh dash; do command -v $s >/dev/null 2>&1 && echo $s; done" }, 20000);
        W($"  [诊断] 直接 exec exit={rawExec.ExitCode} success={rawExec.Success} err=\"{rawExec.ErrorMessage}\"");
        W($"  [诊断] stdout=[{rawExec.StdOut.Trim()}]  stderr=[{rawExec.StdErr.Trim()}]");

        var net = await WslcService.InspectNetworkAsync(runC.Name);
        W($"容器 {runC.Name} 网络: {net.NetworkName} IP={net.Ip} 网关={net.Gateway} 端口={net.PortsText}");
        W($"  断言 取到 IP             : {(net.HasIp ? "PASS" : "!! FAIL")}");
        W($"  断言 IP 形如 172.x       : {(net.Ip.StartsWith("172.") ? "PASS" : "!! FAIL")}");
    }

    Sec("12. 镜像删除结果解读（exit=0 不等于删掉了）");
    // 两组真实输出（2026-10-10 实测逐字抓取）
    const string rmDeleted =
        "已取消标记: mirror.example.com/library/busybox:latest\r\n" +
        "已取消标记: mirror.example.com/library/busybox@sha256:fd7dc98638c8\r\n" +
        "已删除: sha256:aaef90e065235eb0b2d9938be85d1b81add2902cf89affe2cdfa19be33476ac0\r\n" +
        "已删除: sha256:6cd030ace585499136a675899c23aeddcf4ae8065a8d35f016a132bfdc3b4aed";
    const string rmUntagOnly =
        "已取消标记: mirror.example.com/library/busybox:latest\r\n" +
        "已取消标记: mirror.example.com/library/busybox@sha256:fd7dc98638c8";
    const string rmNotFound =
        "来自引擎的错误信息: No such image: mirror.example.com/library/alpine:latest\r\n" +
        "错误代码： WSLC_E_IMAGE_NOT_FOUND";

    void CheckRm(string label, bool ok, string outp, WslcService.ImageRemoveOutcome want)
    {
        var got = WslcService.InterpretImageRemove(ok, outp);
        W($"{label} → {got}  : {(got == want ? "PASS" : $"!! FAIL（期望 {want}）")}");
    }
    CheckRm("无容器占用（有「已删除」行）", true, rmDeleted, WslcService.ImageRemoveOutcome.Deleted);
    CheckRm("被容器占用（只有「取消标记」）", true, rmUntagOnly, WslcService.ImageRemoveOutcome.UntaggedOnly);
    CheckRm("镜像不存在（exit=1）        ", false, rmNotFound, WslcService.ImageRemoveOutcome.Failed);
    CheckRm("英文 Deleted               ", true, "Untagged: foo:latest\r\nDeleted: sha256:abc",
        WslcService.ImageRemoveOutcome.Deleted);
    CheckRm("英文 Untagged only         ", true, "Untagged: foo:latest",
        WslcService.ImageRemoveOutcome.UntaggedOnly);
    CheckRm("空输出 exit=0              ", true, "", WslcService.ImageRemoveOutcome.Deleted);

    Sec("13. 镜像占用对账（容器清单 → 镜像标记）");
    var imgA = new ImageInfo { Repository = "mirror.example.com/library/alpine", Tag = "latest", Id = "320994c3b997deadbeef" };
    var imgB = new ImageInfo { Repository = "mirror.example.com/redis", Tag = "latest", Id = "992eb34c6118cafe0000" };
    var imgC = new ImageInfo { Repository = "mirror2.example.com/library/alpine", Tag = "latest", Id = "320994c3b997deadbeef" };
    var probeImgs = new List<ImageInfo> { imgA, imgB, imgC };
    var probeCtrs = new List<ContainerInfo>
    {
        new() { Name = "app-1", Image = "mirror.example.com/library/alpine:latest" },   // tag 全名
        new() { Name = "app-2", Image = "mirror.example.com/library/alpine" },          // 只有仓库名
        new() { Name = "app-3", Image = "mirror.example.com/libr…" },                   // 表格视图截断
        new() { Name = "app-4", Image = "320994c3b997" },                           // 短 ID
        new() { Name = "redis-1", Image = "mirror.example.com/redis:latest" },
        new() { Name = "idle-1", Image = "无关镜像:2020" },
    };
    foreach (var c in probeCtrs)
        c.ApplyStatus(c.Name == "redis-1" ? "running" : "exited",
                      c.Name == "redis-1" ? "Up 2 minutes" : "Exited (0)");
    WslcService.AttachImageUsage(probeImgs, probeCtrs);
    foreach (var i in probeImgs)
        W($"  {i.Reference,-40} InUse={i.InUse,-6} \"{i.UsageText}\"  {i.UsageBrushKey}");
    W($"  断言 alpine(mirror) 认出 4 个容器: {(imgA.Usage.Total == 4 ? "PASS" : $"!! FAIL（实际 {imgA.Usage.Total}）")}");
    W($"  断言 短 ID 同时匹配两个同 ID 镜像: {(imgA.InUse && imgC.InUse ? "PASS" : "!! FAIL")}");
    W($"  断言 redis 有 1 个运行中        : {(imgB.Usage.RunningCount == 1 && imgB.Usage.StoppedCount == 0 ? "PASS" : "!! FAIL")}");
    W($"  断言 redis 用警告色（运行中）   : {(imgB.UsageBrushKey == "WarnBrush" ? "PASS" : "!! FAIL")}");
    W($"  断言 alpine 全是已停止→信息色   : {(imgA.UsageBrushKey == "InfoBrush" ? "PASS" : "!! FAIL")}");
    W($"  断言 无关镜像未误配             : {(probeImgs.Sum(i => i.Usage.Total) == 6 ? "PASS" : $"!! FAIL（求和 {probeImgs.Sum(i => i.Usage.Total)}）")}");

    Sec("14. 真实环境：运行中的容器 → 镜像占用标记");
    if (!WslcService.Available)
    {
        W("wslc 不可用，跳过。");
    }
    else
    {
        var (imgsNow, _) = await WslcService.ListImagesAsync();
        var (ctrsNow, _) = await WslcService.ListContainersAsync(true);
        WslcService.AttachImageUsage(imgsNow, ctrsNow);
        W($"镜像 {imgsNow.Count} 个，容器 {ctrsNow.Count} 个");
        foreach (var i in imgsNow)
            W($"  {i.Reference,-40} \"{i.UsageText}\"  {i.UsageBrushKey}");
        var inUse = imgsNow.Where(i => i.InUse).ToList();
        W($"被占用的镜像 {inUse.Count} 个");
        var anyRunning = ctrsNow.Any(c => c.IsRunning);
        W($"  断言 运行中容器占用的镜像都被标出: " +
          $"{(anyRunning ? (inUse.Count > 0 ? "PASS" : "!! FAIL") : "N/A（当前没有运行中的容器）")}");
    }

    Sec("15. 端到端：走生产代码路径真拉一次（验证 library/ 补全确实生效）");
    // ⚠ 这一节会**真的联网拉取镜像**，所以必须显式开启：
    //       set WSLCM_PROBE_MIRROR=<你自测可用的加速前缀>
    //   不设就跳过 —— 程序虽然预置了几个公开加速地址，但**探针不该自作主张替用户去连某一家**
    //   （自检要可重复、无外部依赖；真实拉取失败只说明该站此刻不通，不该让探针变红）。
    //   不带前缀（WSLCM_PROBE_MIRROR=official）时走官方源，国内通常拉不动，仅作连通性观察。
    var probeMirror = Environment.GetEnvironmentVariable("WSLCM_PROBE_MIRROR")?.Trim() ?? "";
    if (!WslcService.Available || probeMirror.Length == 0)
    {
        W("未设置 WSLCM_PROBE_MIRROR（或 wslc 不可用），跳过联网拉取。");
        W("  需要验证时：set WSLCM_PROBE_MIRROR=你的加速前缀后重跑。");
    }
    else
    {
        var prefix = probeMirror.Equals("official", StringComparison.OrdinalIgnoreCase) ? "" : probeMirror;
        const string image = "busybox:latest";        // 官方镜像 → 应自动补 library/
        var logLines = new List<string>();
        var rr = await WslcService.PullAsync(image, prefix, s => logLines.Add(s));
        foreach (var l in logLines) W("  " + l);
        W($"  实际退出码 = {rr.ExitCode}  成功 = {rr.Success}");

        if (prefix.Length > 0)
        {
            var usedLib = logLines.Any(l => l.Contains($"{prefix}/library/{image}"));
            W($"  断言 首条加速目标带 library/ : {(usedLib ? "PASS" : "!! FAIL（未补 library/）")}");

            // 参考项（不判 FAIL）：同一站点用裸名直拉。多数站点对裸名回 denied；
            // 若这里也通，只说明该站对两种写法都容错，不影响上面的结论。
            var naked = ProcRunner.Run(WslcService.ExePath,
                new[] { "pull", $"{prefix}/{image}" }, 60000);
            W($"  参考 裸名直拉 exit={naked.ExitCode}（官方镜像的规范名是 library/…）");

            // 「检测加速地址可用性」按钮走的就是 TestMirrorAsync ——
            // 它现在也按候选序列尝试，必须与拉取结论一致（此前不一致正是本 bug 的来源）。
            var detect = await WslcService.TestMirrorAsync(prefix);
            W($"  检测路径 TestMirrorAsync(\"{prefix}\") = {detect}");
            W($"  断言 检测结论 == 拉取结论     : " +
              $"{(detect == rr.Success ? "PASS（两者同源，不会再出现「检测可用但拉取失败」）" : "!! FAIL")}");
        }
    }

    // ── 16. 启动复查 ────────────────────────────────────────────────
    // 背景（2026-10-10 用户报的 bug）：两个容器点「启动」提示成功，列表却仍是「已停止」。
    // 真因不是界面没刷新 —— `wslc container start` 对 Cmd 为 `/bin/sh` 且没加 `-i -t` 的容器
    // 照样 exit=0（容器活了 2 毫秒就退），而界面此前只凭退出码就报「启动成功」。
    // 所以「启动成功」必须靠 inspect 复查运行态得出。本节验证复查所依赖的两级：解析 + 判定。
    int s16pass = 0, s16fail = 0;
    void A16(string label, bool ok)
    {
        if (ok) s16pass++; else s16fail++;
        W($"  断言 {label,-34}: {(ok ? "PASS" : "!! FAIL")}");
    }

    Sec("16. 启动复查（exit=0 只代表「指令被接受」，不代表容器在跑）");

    // 下面两段 JSON 是**真实实测输出**裁掉无关字段后的样子，不是编造的样例。
    const string jsonRunning =
        "[{\"Config\":{\"Cmd\":[\"sleep\",\"300\"],\"User\":\"\"}," +
        "\"State\":{\"ExitCode\":0,\"Running\":true,\"Status\":\"running\"}}]";
    const string jsonExited =
        "[{\"Config\":{\"Cmd\":[\"/bin/sh\"],\"Image\":\"<镜像>\",\"User\":\"\"}," +
        "\"State\":{\"ExitCode\":0,\"FinishedAt\":\"2026-10-09T19:03:45.632935313Z\"," +
        "\"Running\":false,\"StartedAt\":\"2026-10-09T19:03:45.630847468Z\",\"Status\":\"exited\"}}]";

    var stRun = WslcService.ParseStateInfo(jsonRunning);
    A16("解析 running: Queried", stRun.Queried);
    A16("解析 running: Running=true", stRun.Running);
    A16("解析 running: Cmd 拼成 \"sleep 300\"", stRun.Cmd == "sleep 300");

    var stDead = WslcService.ParseStateInfo(jsonExited);
    A16("解析 exited: Queried", stDead.Queried);
    A16("解析 exited: Running=false", !stDead.Running);
    A16("解析 exited: ExitCode=0", stDead.ExitCode == 0);
    A16("解析 exited: Status=exited", stDead.Status == "exited");
    A16("解析 exited: Cmd=/bin/sh", stDead.Cmd == "/bin/sh");

    // ⚠ 这条是本次 bug 的核心判据：指令成功 + 复查未运行 = 必须报「起后即退」而不是「成功」。
    bool wouldWarn = stDead.Queried && !stDead.Running;
    A16("指令成功但未运行 → 判为起后即退", wouldWarn);
    A16("正常运行的容器 → 不误报", !(stRun.Queried && !stRun.Running));

    // 问不到时必须**放弃判断**：宁可什么都不提示，也不能误报「起后即退」。
    A16("空输入: Queried=false（不误判）", !WslcService.ParseStateInfo("").Queried);
    A16("垃圾输入: Queried=false（不误判）", !WslcService.ParseStateInfo("not json at all").Queried);

    // `wslc run -d` 输出容器 ID（实测 64 位十六进制单独占一行）；用户没起名时靠它复查。
    const string runOut = "e5b68e0cff5016780c38d9000b85753287f5bf3a4014858241971a2b6c1777de\n";
    A16("抓 run 输出的容器 ID", WslcService.ParseCreatedId(runOut) == runOut.Trim());
    A16("告警行不会被当成 ID", WslcService.ParseCreatedId("警告: 某行提示\n" + runOut) == runOut.Trim());

    A16("交互式 shell 识别 /bin/sh", WslcService.LooksLikeInteractiveShell("/bin/sh"));
    A16("交互式 shell 识别 bash -l", WslcService.LooksLikeInteractiveShell("bash -l"));
    A16("非交互命令 sleep 300 不误判", !WslcService.LooksLikeInteractiveShell("sleep 300"));
    A16("空命令不误判", !WslcService.LooksLikeInteractiveShell(""));

    W($"  第 16 节小结: {s16pass} PASS / {s16fail} FAIL");

    // 端到端（有副作用，必须显式开启）：对指定容器真跑一次 start 再复查，
    // 且要求复查结论与界面即将给出的文案一致。
    var probeContainer = Environment.GetEnvironmentVariable("WSLCM_PROBE_CONTAINER")?.Trim() ?? "";
    if (probeContainer.Length == 0 || !WslcService.Available)
    {
        W("  端到端跳过：未设置 WSLCM_PROBE_CONTAINER（或 wslc 不可用）。");
        W("  需要验证时：set WSLCM_PROBE_CONTAINER=<容器名> 后重跑。");
    }
    else
    {
        var sr = await WslcService.StartAsync(probeContainer);
        await Task.Delay(600);   // 与界面 VerifyRunningAsync 的沉降窗口同量级
        var live = await WslcService.InspectStateAsync(probeContainer);
        W($"  start exit={sr.ExitCode} 成功={sr.Success}");
        W($"  复查 Queried={live.Queried} Running={live.Running} " +
          $"ExitCode={(live.ExitCode.HasValue ? live.ExitCode.Value.ToString() : "null")} Cmd=\"{live.Cmd}\"");
        W($"  界面将显示: {(live.Queried && !live.Running ? $"「容器随即退出（退出码 …）」" : "「启动成功」")}");
        W($"  断言 复查结论可用于判定        : " +
          $"{(live.Queried && (live.Running || live.ExitCode.HasValue) ? "PASS" : "!! FAIL")}");
        W($"  断言 exit=0 不直接当成功       : " +
          $"{(sr.Success ? "PASS（exit=0 已知，判定改由复查给出）" : "参考：本次 start 本身就非 0")}");
    }

    // ══ 17. 备份元数据 → 重建容器 的解析 ══
    // 这一节存在的理由：这套解析原先长在界面点击回调里，只能靠人手点一次弹窗才验得到，
    // 于是字段名猜错了两版都没人发现 —— **真实 inspect 输出里端口映射在顶层 `Ports`**，
    // `HostConfig` 下根本没有 `PortBindings`，导致「按原配置恢复」恢复出来的容器永远丢端口。
    int s17pass = 0, s17fail = 0;
    void A17(string label, bool ok)
    {
        if (ok) s17pass++; else s17fail++;
        W($"  断言 {label,-38}: {(ok ? "PASS" : "!! FAIL")}");
    }

    Sec("17. 备份元数据解析（按原配置恢复容器）");

    // 下面是 2026-10-12 `wslc inspect wslcm_bugcheck -f json` 抓到的**逐字**输出，只是压成一行。
    const string realInspect = """[{"Config":{"Cmd":["sleep","60"],"Entrypoint":null,"Env":["FOO=bar","PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin"],"Healthcheck":null,"Image":"docker.1ms.run/library/alpine:latest","Labels":{},"StopTimeout":null,"User":"","WorkingDir":"/"},"Created":"2026-10-12T11:18:55.670168447Z","HostConfig":{"Memory":0,"NanoCpus":0,"NetworkMode":"bridge","Ulimits":[]},"Id":"a11d89d8e0329460edc6a60b05075ce12b40563bdddab6ff8593e06bb0a500ec","Image":"sha256:320994c3b997e2ec6433f717f153e108023c5bec8fefa8d76b83451d16d05ea8","Labels":{},"Mounts":[],"Name":"/wslcm_bugcheck","NetworkSettings":{"Networks":{"bridge":{"Aliases":[],"DriverOpts":{},"Gateway":"172.17.0.1","IPAMConfig":null,"IPAddress":"172.17.0.2","IPPrefixLen":16,"Links":[],"MacAddress":"02:42:ac:11:00:02"}}},"Ports":{"80/tcp":[{"HostIp":"127.0.0.1","HostPort":"18080"}]},"State":{"ExitCode":0,"FinishedAt":"0001-01-01T00:00:00Z","Health":null,"Running":true,"StartedAt":"2026-10-12T11:18:55.971368418Z","Status":"running"}}]""";

    var plan = RestorePlanner.FromInspectJson(realInspect, "bugcheck:restored", _ => false);
    A17("名称取自 Name（去掉前导 /）", plan.Spec.Name == "wslcm_bugcheck");
    A17("启动命令拼回 \"sleep 60\"", plan.Spec.Command == "sleep 60");
    A17("环境变量含 FOO=bar", plan.Spec.Env.Any(e => e.Key == "FOO" && e.Value == "bar"));
    A17("端口映射 1 条", plan.Spec.Ports.Count == 1);
    A17("端口 = 宿主 18080 → 容器 80", plan.Spec.Ports.Count == 1 &&
        plan.Spec.Ports[0].HostPort == "18080" && plan.Spec.Ports[0].ContainerPort == "80");
    A17("下发参数里带 -p 18080:80", plan.Spec.ToArgs().Contains("18080:80"));
    A17("镜像用调用方给的重建镜像", plan.Spec.Image == "bugcheck:restored");

    // 把根因固化成断言：真实输出里 HostConfig 下**没有** PortBindings
    using (var probeDoc = System.Text.Json.JsonDocument.Parse(realInspect))
    {
        var root0 = probeDoc.RootElement[0];
        var hc0 = root0.GetProperty("HostConfig");
        A17("HostConfig 下没有 PortBindings（旧假设位置）", !hc0.TryGetProperty("PortBindings", out _));
        A17("顶层 Ports 存在（真实位置）", root0.TryGetProperty("Ports", out _));
    }

    // 名字冲突 → 自动加后缀；无启动命令 → 必须给出提醒
    var planDup = RestorePlanner.FromInspectJson(realInspect, "bugcheck:restored",
        n => n == "wslcm_bugcheck");
    A17("名字被占用时加 -restored", planDup.Spec.Name == "wslcm_bugcheck-restored");
    var planEmpty = RestorePlanner.FromInspectJson("""{"Name":"/x","Config":{"Cmd":[]}}""", "empty:1");
    A17("无启动命令时给出提醒", planEmpty.Notes.Count > 0);
    A17("无端口时不虚构映射", planEmpty.Spec.Ports.Count == 0);

    W($"  第 17 节小结: {s17pass} PASS / {s17fail} FAIL");

    // ══ 17b. 虚拟磁盘压缩的「真假成功」判定 ══
    // 背景：diskpart 脚本是顺序执行的，`compact vdisk` 失败**不会**中断脚本，
    // 后面的 `detach vdisk` 照旧成功并写下「已成功分离虚拟磁盘文件」。
    // 于是 `exit==0 && 含"successfully" || 含"成功"` 这种写法会把 detach 的成功
    // 当成 compact 的成功 —— 界面弹「压缩完成，无碎片可回收」，实际什么都没压。
    Sec("17b. 虚拟磁盘压缩的「真假成功」判定（假成功回归）");
    int s17bpass = 0, s17bfail = 0;
    void A17b(string label, bool ok)
    {
        if (ok) s17bpass++; else s17bfail++;
        W($"  断言 {label,-38}: {(ok ? "PASS" : "!! FAIL")}");
    }

    // 真实成功时的输出（diskpart 中文界面；命令回显 + 结果逐行）
    const string compactOk = """
        在计算机上: WSLC-BOX

        DISKPART> select vdisk file="C:\Users\x\AppData\Local\wslc\sessions\wslc-cli-x\storage.vhdx"

        已成功选择虚拟磁盘文件。

        DISKPART> attach vdisk readonly

          100 百分比已完成

        已成功连接虚拟磁盘文件。

        DISKPART> compact vdisk

          100 百分比已完成

        DiskPart 已成功压缩虚拟磁盘文件。

        DISKPART> detach vdisk

        已成功分离虚拟磁盘文件。

        DISKPART> exit
        """;

    // 失败时的输出：compact 报错，但脚本继续、detach 依旧「成功」
    const string compactFail = """
        在计算机上: WSLC-BOX

        DISKPART> select vdisk file="C:\Users\x\AppData\Local\wslc\sessions\wslc-cli-x\storage.vhdx"

        已成功选择虚拟磁盘文件。

        DISKPART> attach vdisk readonly

        已成功连接虚拟磁盘文件。

        DISKPART> compact vdisk

        DiskPart 遇到错误: 介质受写入保护。
        有关详细信息，请参阅系统事件日志。

        DISKPART> detach vdisk

        已成功分离虚拟磁盘文件。

        DISKPART> exit
        """;

    var jOk = WSLCManager.Helpers.DiskCompactor.JudgeCompact(0, compactOk);
    A17b("正常成功 → 判成功", jOk.Ok);

    var jFail = WSLCManager.Helpers.DiskCompactor.JudgeCompact(0, compactFail);
    A17b("compact 报错但 detach 成功 → 判**失败**", !jFail.Ok);
    A17b("失败原因指向「错误」", jFail.Why.Contains("错误"));

    // 旧写法在这里会判成成功 —— 把这一点也固化下来，防止有人改回去
    var oldWay = compactFail.Contains("成功");
    A17b("旧写法（含「成功」即算过）会误判", oldWay);

    A17b("空输出 → 判失败，不猜", !WSLCManager.Helpers.DiskCompactor.JudgeCompact(0, "").Ok);
    A17b("英文 success 也认", WSLCManager.Helpers.DiskCompactor.JudgeCompact(0,
        "select vdisk file=\"x\"\ncompact vdisk\n100 percent completed\nDiskPart successfully compacted the virtual disk file.").Ok);
    A17b("非 0 返回码 → 判失败", !WSLCManager.Helpers.DiskCompactor.JudgeCompact(1, compactOk).Ok);

    W($"  第 17b 节小结: {s17bpass} PASS / {s17bfail} FAIL");

    // ══ 18 / 19 ══
    Sec("18. 网络速率采样（NetMeter）");
    {
        var a = WSLCManager.Helpers.NetMeter.TotalBytesReceived();
        W($"  t0 累计接收: {a} bytes ({WSLCManager.Helpers.NetMeter.Human(a)})");
        await Task.Delay(3000);
        var b = WSLCManager.Helpers.NetMeter.TotalBytesReceived();
        W($"  t3 累计接收: {b} bytes，3 秒增量 {b - a} bytes => {(b - a) / 3.0 / 1024:F1} KB/s");
        W($"  是否可用（非 -1）: {a >= 0 && b >= 0}");
    }

    Sec("19. 磁盘占用（vhdx 只增不减的现状）");
    W($"  storage bytes = {WslcService.GetStorageBytes()}");

    // ══ 20. 拉取中途取消 ══
    // ⚠ 这一节会**删掉一个镜像并真拉 nginx（数百 MB）**，属于"改动本机数据 + 联网"的自检，
    //   所以默认不跑、必须显式开两个开关（与第 15 节同一原则）。
    Sec("20. 拉取中途取消（验证不再误判为加速失败）");
    {
        var pullOn = (Environment.GetEnvironmentVariable("WSLCM_PROBE_PULL") ?? "").Trim();
        var prefix20 = probeMirror.Length == 0 ||
                       probeMirror.Equals("official", StringComparison.OrdinalIgnoreCase)
            ? "" : probeMirror;

        if (pullOn != "1" || prefix20.Length == 0 || !WslcService.Available)
        {
            W("  跳过：需同时设置 WSLCM_PROBE_PULL=1 与 WSLCM_PROBE_MIRROR=<加速前缀>。");
            W($"  当前 WSLCM_PROBE_PULL=\"{pullOn}\"　WSLCM_PROBE_MIRROR=\"{probeMirror}\"");
            W("  该节会先删镜像再真拉 nginx，属「改动本机 + 联网」的自检，故默认不执行。");
        }
        else
        {
            // 先删掉，确保真的在下载（否则本地已有会秒回）
            ProcRunner.Run(WslcService.ExePath,
                new[] { "image", "rm", "-f", $"{prefix20}/library/nginx:latest" }, 60000);

            var cts = new CancellationTokenSource();
            var lines = new List<string>();
            var task = WslcService.PullAsync("nginx:latest", prefix20,
                s2 => { lock (lines) lines.Add(s2); }, 0, cts.Token);
            await Task.Delay(2500);
            W("  2.5 秒后发出取消信号…");
            cts.Cancel();
            var rc = await task;
            W($"  Canceled = {rc.Canceled}　Success = {rc.Success}　ExitCode = {rc.ExitCode}");
            W($"  期间输出 {lines.Count} 行：");
            foreach (var l in lines.Take(8)) W("     " + l);
            W($"  断言 取消后判为「用户停止」而非失败 : " +
              $"{(rc.Canceled && !rc.Success ? "PASS" : "!! FAIL")}");
        }
    }

    // ══ 21. 网络列表与详情解析 ══
    // 夹具取自 2026-10-13 本机实测输出。这一块最容易写错的地方是**两路分工**：
    //   · network ls --format json  → NDJSON，只有 ID/名称/驱动/范围，**根本没有网段**
    //   · network inspect <名...> -f json → 一个 JSON 数组，才带 IPAM 网段与 Containers
    // 所以「网段为空」不是 bug，是没补 inspect；下面的断言把这条钉死。
    Sec("21. 网络列表与详情解析（ls + inspect 两路合并）");
    int s21pass = 0, s21fail = 0;
    void A21(string label, bool ok)
    {
        if (ok) s21pass++; else s21fail++;
        W($"  断言 {label,-42}: {(ok ? "PASS" : "!! FAIL")}");
    }

    const string netLsJson = """
        {"CreatedAt":"2026-10-13 16:26:40.233599799 +0000 UTC","Driver":"bridge","ID":"ea84fe08d007","IPv4":"true","IPv6":"false","Internal":"false","Labels":"","Name":"bridge","Scope":"local"}
        {"CreatedAt":"2026-10-12 15:40:37.398394786 +0000 UTC","Driver":"host","ID":"5e5a8d1074be","IPv4":"true","IPv6":"false","Internal":"false","Labels":"","Name":"host","Scope":"local"}
        {"CreatedAt":"2026-10-12 15:40:37.383597901 +0000 UTC","Driver":"null","ID":"f6ff3153271d","IPv4":"true","IPv6":"false","Internal":"false","Labels":"","Name":"none","Scope":"local"}
        {"CreatedAt":"2026-10-13 16:27:25.300710107 +0000 UTC","Driver":"bridge","ID":"84fb27ff4c87","IPv4":"true","IPv6":"false","Internal":"false","Labels":"","Name":"mytest-net","Scope":"local"}
        """;

    var netList = WslcService.ParseNetworkList(netLsJson);
    A21("ls JSON 解析出 4 个网络", netList.Count == 4);
    A21("内置网络排最前（bridge）", netList.Count > 0 && netList[0].Name == "bridge");
    A21("自建网络排在内置之后", netList.Count > 0 && netList[^1].Name == "mytest-net");
    A21("内置 bridge 不允许删", netList.Count > 0 && !netList[0].CanRemove);
    A21("自建网络允许删", netList.Count > 0 && netList[^1].CanRemove);
    A21("驱动 null → 文案「none 无网络」", netList.Any(n => n.DriverText == "none 无网络"));
    A21("ls 阶段网段为空（本来就没有）", netList.All(n => n.Subnet.Length == 0));

    // inspect 输出的字段与实测一致（IPAM.Config[0] 里才是 Subnet/Gateway）
    const string netInspectJson = """
        [{"Attachable":false,"ConfigOnly":false,"Containers":{},"Created":"2026-10-13T16:27:19.079432526Z","Driver":"bridge","EnableIPv4":true,"EnableIPv6":false,"IPAM":{"Config":[{"Gateway":"172.17.0.1","Subnet":"172.17.0.0/16"}],"Driver":"default","Options":{}},"Id":"27db8600f40ff4df1a1494368df8804a2312db8997456febcfaf2633d42b200f","Ingress":false,"Internal":false,"Labels":{},"Name":"bridge","Options":{},"Scope":"local","Status":{}},{"Attachable":false,"Containers":{},"Created":"2026-10-13T16:27:25.300710107Z","Driver":"bridge","EnableIPv4":true,"EnableIPv6":false,"IPAM":{"Config":[{"Gateway":"172.30.0.1","Subnet":"172.30.0.0/24"}],"Driver":"default","Options":{}},"Id":"84fb27ff4c873feb8b452d051b50a69178c2363e30d06692b9df69883128f3ee","Internal":false,"Labels":{},"Name":"mytest-net","Options":{},"Scope":"local","Status":{}}]
        """;

    WslcService.ApplyNetworkDetails(netList, netInspectJson);
    var netBridge = netList.First(n => n.Name == "bridge");
    var netMine = netList.First(n => n.Name == "mytest-net");

    A21("补详情后 bridge 网段 172.17.0.0/16", netBridge.Subnet == "172.17.0.0/16");
    A21("bridge 网关 172.17.0.1", netBridge.Gateway == "172.17.0.1");
    A21("自定义网段 172.30.0.0/24 被读到", netMine.Subnet == "172.30.0.0/24");
    A21("自定义网关 172.30.0.1 被读到", netMine.Gateway == "172.30.0.1");
    A21("ID 被补成完整 64 位", netBridge.Id.Length == 64);
    A21("创建时间归一成本地 yyyy-MM-dd HH:mm", netBridge.Created.Length == 16);
    A21("没 inspect 到的 host 仍标未加载", !netList.First(n => n.Name == "host").DetailLoaded);
    A21("网段缺失时给中性占位而非空白", new WSLCManager.Models.NetworkInfo().SubnetText == "—");
    // 内置 host/none 根本没有网段，说「自动分配」是误导 —— 只能是「—」
    A21("内置 host 无网段 → 「—」", netList.First(n => n.Name == "host").SubnetText == "—");
    A21("自建网络未取详情时也给「—」",
        new WSLCManager.Models.NetworkInfo { Name = "app-net" }.SubnetText == "—");
    A21("自建网络取到详情且无网段才说「自动分配」",
        new WSLCManager.Models.NetworkInfo { Name = "app-net", DetailLoaded = true }.SubnetText == "自动分配");

    // 网络列表用来标记「已存在的坏网段」——与创建校验共用同一个判定
    A21("ExplainSubnet 对回环段给出原因", NetworkValidator.ExplainSubnet("127.11.0.0/24").Contains("回环"));
    A21("ExplainSubnet 对正常段返回空", NetworkValidator.ExplainSubnet("172.30.0.0/24") == "");
    A21("ExplainSubnet 对留空返回空（自动分配）", NetworkValidator.ExplainSubnet("") == "");
    A21("ExplainSubnet 对畸形 CIDR 也报错", NetworkValidator.ExplainSubnet("172.30.0.0/99").Length > 0);
    A21("列表标记与创建校验同源（两边都拒回环段）",
        NetworkValidator.ExplainSubnet("127.11.0.0/24").Length > 0 &&
        !NetworkValidator.Validate("n1", "127.11.0.0/24", "", out _));

    var netBad = new WSLCManager.Models.NetworkInfo
    { Name = "a1231", Subnet = "127.11.0.0/24", DetailLoaded = true };
    netBad.SubnetWarning = NetworkValidator.ExplainSubnet(netBad.Subnet);
    A21("NetworkInfo 能带上坏网段警告", netBad.HasSubnetWarning);
    A21("正常网络不带警告", !new WSLCManager.Models.NetworkInfo().HasSubnetWarning);

    // 关联容器：inspect 的 Containers 是个 map（键=容器 ID），值里才有 Name 与 IPv4Address
    const string netWithCtr = """
        [{"Containers":{"f70880de115e":{"Name":"nginx-test","EndpointID":"e1","MacAddress":"02:42:ac:11:00:02","IPv4Address":"172.17.0.2/16","IPv6Address":""},"aaaaaaaaaaaa":{"Name":"web-1","EndpointID":"e2","MacAddress":"02:42:ac:1e:00:02","IPv4Address":"172.30.0.2/24","IPv6Address":""}},"Driver":"bridge","IPAM":{"Config":[{"Gateway":"172.30.0.1","Subnet":"172.30.0.0/24"}]},"Id":"84fb27ff4c873feb8b452d051b50a69178c2363e30d06692b9df69883128f3ee","Name":"app-net","Scope":"local"}]
        """;

    var l2 = WslcService.ParseNetworkList("""{"ID":"84fb27ff4c87","Name":"app-net","Driver":"bridge","Scope":"local"}""");
    WslcService.ApplyNetworkDetails(l2, netWithCtr);
    A21("关联容器解析出 2 个", l2[0].ContainerCount == 2);
    A21("容器名取 inspect 的 Name", l2[0].ContainersText.Contains("nginx-test"));
    A21("容器网络内 IP 一并取到", l2[0].Containers.Any(c => c.Ip == "172.17.0.2/16"));
    A21("数量文案「2 个」", l2[0].CountText == "2 个");
    A21("有容器时删除提示会劝住", l2[0].RemoveTip.Contains("2"));

    var l3 = WslcService.ParseNetworkList("""{"ID":"5e5a8d1074be","Name":"host","Driver":"host","Scope":"local"}""");
    WslcService.ApplyNetworkDetails(l3, netInspectJson);
    A21("无关联容器 → 占位符「—」", l3[0].ContainersText == "—");

    // 输出里夹带提示文本时仍要能解析（RunResult.All 是 stdout+stderr 拼起来的）
    var l4 = WslcService.ParseNetworkList("""{"ID":"84fb27ff4c87","Name":"app-net","Driver":"bridge","Scope":"local"}""");
    WslcService.ApplyNetworkDetails(l4, "提示：某行无关输出\n" + netWithCtr + "\n（结束）");
    A21("夹带提示文本仍能解析", l4[0].ContainerCount == 2);

    // 创建前的本地校验
    A21("名称留空被拦", !NetworkValidator.Validate("", "172.30.0.0/24", "", out _));
    A21("名称含 / 被拦", !NetworkValidator.Validate("a/b", "", "", out _));
    A21("占用内置名 bridge 被拦", !NetworkValidator.Validate("bridge", "", "", out _));
    A21("CIDR 缺掩码被拦", !NetworkValidator.Validate("n1", "172.30.0.0", "", out _));
    A21("掩码 33 被拦", !NetworkValidator.Validate("n1", "172.30.0.0/33", "", out _));
    A21("非 IPv4 被拦", !NetworkValidator.Validate("n1", "300.1.1.0/24", "", out _));
    A21("网关不在网段内被拦", !NetworkValidator.Validate("n1", "172.30.0.0/24", "172.31.0.1", out _));
    A21("只填网关不填网段被拦", !NetworkValidator.Validate("n1", "", "172.30.0.1", out _));
    A21("合法输入通过", NetworkValidator.Validate("app-net", "172.30.0.0/24", "172.30.0.1", out _));
    A21("网段留空通过（自动分配）", NetworkValidator.Validate("app-net", "", "", out _));
    A21("/32 边界掩码通过", NetworkValidator.Validate("n1", "10.0.0.1/32", "10.0.0.1", out _));

    // ↓ 2026-10-14 实测踩到的坑：`wslc network create` **不校验网段** ——
    //   127.11.0.0/24 建得出来（exit=0、列表里也正常），直到 run 才报
    //   「route for the gateway 127.11.0.1 could not be found」。以下断言把「当地就该拦住」固化。
    A21("回环段 127.11.0.0/24 被拦",
        !NetworkValidator.Validate("n1", "127.11.0.0/24", "127.11.0.1", out var w127));
    A21("回环提示里点明「回环」", w127.Contains("回环"));
    A21("0.0.0.0/8 段被拦", !NetworkValidator.Validate("n1", "0.1.2.0/24", "", out _));
    A21("链路本地 169.254/16 被拦", !NetworkValidator.Validate("n1", "169.254.1.0/24", "", out _));
    A21("多播 224.0.0.0/4 被拦", !NetworkValidator.Validate("n1", "224.1.0.0/24", "", out _));
    A21("保留段 240.0.0.0/4 被拦", !NetworkValidator.Validate("n1", "240.1.0.0/24", "", out _));
    A21("10.0.0.0/8 通过（别和回环混淆误拦）",
        NetworkValidator.Validate("n1", "10.0.0.0/8", "", out _));
    A21("172.30.0.0/24 通过", NetworkValidator.Validate("n1", "172.30.0.0/24", "", out _));
    A21("192.168.99.0/24 通过", NetworkValidator.Validate("n1", "192.168.99.0/24", "", out _));
    A21("主机位非零 172.30.0.5/24 被拦",
        !NetworkValidator.Validate("n1", "172.30.0.5/24", "", out var wHost));
    A21("并提示正确的网络地址 172.30.0.0/24", wHost.Contains("172.30.0.0/24"));
    A21("掩码抹零正确：172.30.0.5/24 → 172.30.0.0", NetworkValidator.Masked(
        System.Net.IPAddress.Parse("172.30.0.5"), 24).ToString() == "172.30.0.0");
    A21("掩码 0 不当成「全 1」（C# 移位按 32 取模的坑）",
        NetworkValidator.Masked(System.Net.IPAddress.Parse("10.20.30.40"), 0).ToString() == "0.0.0.0");
    A21("同级相邻网段判定正确", NetworkValidator.InSameSubnet(
        System.Net.IPAddress.Parse("172.30.0.1"), System.Net.IPAddress.Parse("172.30.0.200"), 24));
    A21("跨网段不误判为同段", !NetworkValidator.InSameSubnet(
        System.Net.IPAddress.Parse("172.30.0.1"), System.Net.IPAddress.Parse("172.31.0.1"), 24));

    W($"  第 21 节小结: {s21pass} PASS / {s21fail} FAIL");

    // ══ 22. 镜像存档参数 + 容器网络参数 ══
    // image save/load 是「几百 MB 级」的操作，不可能每次自检都真跑。
    // 抽成纯函数后，这里只钉参数形状（顺序、选项名、去空格）。
    Sec("22. 镜像存档参数与容器网络参数");
    int s22pass = 0, s22fail = 0;
    void A22(string label, bool ok)
    {
        if (ok) s22pass++; else s22fail++;
        W($"  断言 {label,-42}: {(ok ? "PASS" : "!! FAIL")}");
    }

    var sv = WslcService.BuildImageSaveArgs("  nginx:1.27  ", "  D:\\out\\nginx.tar  ");
    A22("导出参数序列正确", string.Join(" ", sv) == "image save -o D:\\out\\nginx.tar nginx:1.27");
    A22("导出用 -o 而不是 --output", sv.Contains("-o"));

    var ld = WslcService.BuildImageLoadArgs("  D:\\out\\nginx.tar  ");
    A22("导入参数序列正确", string.Join(" ", ld) == "image load -i D:\\out\\nginx.tar");
    A22("导入用 -i 而不是 --input", ld.Contains("-i"));

    var specNet = new ContainerSpec { Image = "alpine:latest", Name = "probe-net", Network = "app-net" };
    var argsNet = specNet.ToArgs();
    var ni = argsNet.IndexOf("--network");
    A22("指定网络 → 下发 --network", ni >= 0);
    A22("网络名跟在选项之后", ni >= 0 && ni + 1 < argsNet.Count && argsNet[ni + 1] == "app-net");
    A22("命令行预览含 --network", specNet.PreviewCommandLine().Contains("--network app-net"));

    var specNoNet = new ContainerSpec { Image = "alpine:latest", Name = "probe-plain" };
    A22("不选网络 → 完全不下发该参数", !specNoNet.ToArgs().Contains("--network"));
    A22("留空网络不被当成空字符串下发", specNoNet.ToArgs().All(x => x != ""));

    var specBlank = new ContainerSpec { Image = "alpine:latest", Network = "   " };
    A22("空白网络名也不下发", !specBlank.ToArgs().Contains("--network"));

    W($"  第 22 节小结: {s22pass} PASS / {s22fail} FAIL");

    // ══ 23. 开机自启（注册表命令行 + 名单快照往返）══
    // 这一节**不碰真实注册表**：Enable/Disable 会改用户的开机项，自检不该动它。
    // 只验证纯函数（命令行拼装、快照序列化/解析）与路径约定。
    Sec("23. 开机自启：命令行与名单快照（纯函数，不写注册表）");
    int s23pass = 0, s23fail = 0;
    void A23(string label, bool ok)
    {
        if (ok) s23pass++; else s23fail++;
        W($"  断言 {label,-42}: {(ok ? "PASS" : "!! FAIL")}");
    }

    A23("自启参数是 --autostart", AutoStart.AutoStartArgument == "--autostart");
    A23("启动项写在 HKCU CurrentVersion\\Run",
        AutoStart.RegistryKeyPath == "Software\\Microsoft\\Windows\\CurrentVersion\\Run");
    A23("启动项名固定 WSLCManager", AutoStart.EntryName == "WSLCManager");

    var cmdLine = AutoStart.BuildCommandLine(@"C:\Program Files\WSLC\WSLCManager.exe");
    A23("带空格路径会被引号包住", cmdLine.StartsWith("\"C:\\Program Files\\WSLC\\WSLCManager.exe\""));
    A23("命令行尾部带自启参数", cmdLine.EndsWith(" --autostart"));
    A23("空路径 → 空命令行（不当成合法项写入）", AutoStart.BuildCommandLine("") == "");
    A23("名单文件落在配置目录", AutoStart.SnapshotPath.EndsWith("autostart-containers.txt"));

    var snapText = AutoStart.SerializeSnapshot(
        new[] { "web-frontend", "api-server", "api-server", "  " },
        new DateTime(2026, 10, 14, 0, 30, 0));
    A23("快照以注释头开头", snapText.StartsWith("# WSLC 容器管理器"));
    A23("快照带时间戳（仅供排查）", snapText.Contains("2026-10-14 00:30:00"));
    A23("序列化时已去重去空白", snapText.Split('\n').Count(l => l.Trim() == "api-server") == 1);

    var snapBack = AutoStart.ParseSnapshot(snapText);
    A23("往返一致（2 个容器）", snapBack.Count == 2);
    A23("顺序保持不变", snapBack[0] == "web-frontend" && snapBack[1] == "api-server");
    A23("注释行不会被当成容器名", !snapBack.Any(n => n.StartsWith("#")));
    A23("空行与空白被忽略", AutoStart.ParseSnapshot("\n\n  web-1  \n\t\n").Single() == "web-1");
    A23("解析时大小写不敏感去重", AutoStart.ParseSnapshot("Web-1\nweb-1\nweb-2").Count == 2);
    A23("空内容 → 空名单（不抛异常）", AutoStart.ParseSnapshot("").Count == 0);
    A23("用户手加的分号注释也忽略", AutoStart.ParseSnapshot("; 备注\nweb-1").Count == 1);

    // 只有空注释头时（= 关机前没有容器在跑）解析结果必须是空，否则开机自启会去 start 一堆不存在的东西
    var emptySnap = AutoStart.SerializeSnapshot(Array.Empty<string>(), DateTime.Now);
    A23("没有运行中容器时快照解析为空", AutoStart.ParseSnapshot(emptySnap).Count == 0);

    W($"  第 23 节小结: {s23pass} PASS / {s23fail} FAIL");

    // ══ 24. 主题：两份调色板对账 + 对比度 ══
    // 换肤最容易出的两类事故，都不报错、只能靠数值抓：
    //   ① 某一本 palette 少写一个 key → 切换后那处**静默**回退成默认色（黑字/白底）；
    //   ② 深色版照抄浅色值、或对比度不够 → 深色下小字看不清。
    Sec("24. 主题：调色板 key 对账与对比度（数值定案）");
    int s24pass = 0, s24fail = 0;
    void A24(string label, bool ok)
    {
        if (ok) s24pass++; else s24fail++;
        W($"  断言 {label,-46}: {(ok ? "PASS" : "!! FAIL")}");
    }

    string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null)
        {
            if (File.Exists(Path.Combine(d.FullName, "Themes", "Palette.Light.xaml"))) return d.FullName;
            d = d.Parent;
        }
        return null;
    }

    var repo = RepoRoot();
    A24("定位到仓库根目录", repo != null);

    if (repo != null)
    {
        var lightPath = Path.Combine(repo, "Themes", "Palette.Light.xaml");
        var darkPath = Path.Combine(repo, "Themes", "Palette.Dark.xaml");

        // key → 色值（渐变取第一个 GradientStop 当代表色）
        (System.Collections.Generic.Dictionary<string, string> Colors, int Grads) LoadPalette(string path)
        {
            var map = new System.Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal);
            XNamespace xn = "http://schemas.microsoft.com/winfx/2006/xaml";
            int grads = 0;
            foreach (var el in XDocument.Load(path).Root.Elements())
            {
                var key = el.Attribute(xn + "Key")?.Value;
                if (key == null) continue;

                if (el.Name.LocalName == "SolidColorBrush") map[key] = el.Attribute("Color")?.Value ?? "";
                else if (el.Name.LocalName == "Color") map[key] = (el.Value ?? "").Trim();
                else if (el.Name.LocalName == "LinearGradientBrush")
                {
                    map[key] = el.Elements().FirstOrDefault(e => e.Name.LocalName == "GradientStop")
                                  ?.Attribute("Color")?.Value ?? "";
                    grads++;
                }
            }
            return (map, grads);
        }

        var (light, lightGrads) = LoadPalette(lightPath);
        var (dark, darkGrads) = LoadPalette(darkPath);
        W($"  浅色调色板: {light.Count} 个 key（含 {lightGrads} 个渐变）");
        W($"  深色调色板: {dark.Count} 个 key（含 {darkGrads} 个渐变）");

        // ① key 集合必须完全一致
        var missDark = light.Keys.Where(k => !dark.ContainsKey(k)).ToList();
        var missLight = dark.Keys.Where(k => !light.ContainsKey(k)).ToList();
        A24($"深色没漏 key（缺 {missDark.Count} 个）", missDark.Count == 0);
        if (missDark.Count > 0) W("     缺: " + string.Join(", ", missDark));
        A24($"浅色没多 key（多 {missLight.Count} 个）", missLight.Count == 0);
        if (missLight.Count > 0) W("     多: " + string.Join(", ", missLight));
        A24("两本 key 数量一致", light.Count == dark.Count);
        A24($"key 数量 ≥ 30（实际 {light.Count}）", light.Count >= 30);

        bool AllHex(System.Collections.Generic.Dictionary<string, string> m)
            => m.Values.All(v => v.Length == 7 && v[0] == '#' && v.Skip(1).All(Uri.IsHexDigit));
        A24("浅色值全是 #RRGGBB", AllHex(light));
        A24("深色值全是 #RRGGBB", AllHex(dark));

        // ② 对比度（WCAG 相对亮度）
        double Chan(double v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        double Lum(string hex) => 0.2126 * Chan(Convert.ToInt32(hex.Substring(1, 2), 16))
                                + 0.7152 * Chan(Convert.ToInt32(hex.Substring(3, 2), 16))
                                + 0.0722 * Chan(Convert.ToInt32(hex.Substring(5, 2), 16));
        double Contrast(string fg, string bg)
        {
            double a = Lum(fg), b = Lum(bg);
            return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
        }

        foreach (var pair in new[] { ("浅色", light), ("深色", dark) })
        {
            var name = pair.Item1;
            var map = pair.Item2;
            var bg = map["CardBrush"];

            W($"  ── {name}主题（卡片底色 {bg}）──");
            W($"     正文 {map["TextBrush"]} {Contrast(map["TextBrush"], bg):F2}:1   " +
              $"次要 {map["SubTextBrush"]} {Contrast(map["SubTextBrush"], bg):F2}:1   " +
              $"弱化 {map["MutedBrush"]} {Contrast(map["MutedBrush"], bg):F2}:1");
            W($"     强调 {map["AccentBrush"]} 对底 {Contrast(map["AccentBrush"], bg):F2}:1   " +
              $"色块文字 {map["OnAccentFgBrush"]} 对底色块 {Contrast(map["OnAccentFgBrush"], map["AccentBrush"]):F2}:1");

            A24($"{name} 正文 ≥ 7", Contrast(map["TextBrush"], bg) >= 7.0);
            A24($"{name} 次要文字 ≥ 4.5", Contrast(map["SubTextBrush"], bg) >= 4.5);
            A24($"{name} 弱化文字 ≥ 3", Contrast(map["MutedBrush"], bg) >= 3.0);
            A24($"{name} 强调色块上的文字 ≥ 4.5",
                Contrast(map["OnAccentFgBrush"], map["AccentBrush"]) >= 4.5);

            foreach (var k in new[] { "OkBrush", "WarnBrush", "ErrBrush", "InfoBrush" })
            {
                var ratio = Contrast(map[k], bg);
                if (name == "深色") A24($"深色 {k} ≥ 4.5（实际 {ratio:F2}）", ratio >= 4.5);
                // 浅色的绿/橙对白底只有 3.0~3.4:1，是既有配色（状态圆点为主）。
                // 不擅自改用户已经习惯的颜色，这里只打印出来留个记录。
                else W($"     参考 {k} {map[k]} {ratio:F2}:1（既有配色，不判定）");
            }
        }

        // ③ 深色不能是"照抄浅色"：关键色要真变了，且明暗方向正确
        A24("深色 CardBrush ≠ 浅色", dark["CardBrush"] != light["CardBrush"]);
        A24("深色 TextBrush ≠ 浅色", dark["TextBrush"] != light["TextBrush"]);
        A24("深色 AccentBrush ≠ 浅色", dark["AccentBrush"] != light["AccentBrush"]);
        A24("深色底色比浅色底暗", Lum(dark["CardBrush"]) < Lum(light["CardBrush"]));
        A24("深色下文字比底色亮（否则就是黑字糊在深底上）",
            Lum(dark["TextBrush"]) > Lum(dark["CardBrush"]));
        A24("浅色下文字比底色暗", Lum(light["TextBrush"]) < Lum(light["CardBrush"]));
        A24("深色窗口底比卡片更暗（层次方向对）",
            Lum(dark["WindowGradient"]) < Lum(dark["CardBrush"]));

        // ④ 回归守卫：任何 xaml 都不许再出现 {StaticResource <调色板key>}
        //    —— 那种引用在解析时就把对象定死了，换肤时不会跟着更新（且不报错）。
        var keys = new System.Collections.Generic.HashSet<string>(light.Keys, StringComparer.Ordinal);
        int stray = 0;
        var strayWhere = new System.Collections.Generic.List<string>();
        var xamls = Directory.EnumerateFiles(Path.Combine(repo, "Themes"), "*.xaml")
            .Concat(Directory.EnumerateFiles(repo, "*.xaml"))
            .Concat(Directory.EnumerateFiles(Path.Combine(repo, "Dialogs"), "*.xaml"));

        foreach (var f in xamls)
        {
            foreach (Match m in Regex.Matches(File.ReadAllText(f), @"\{StaticResource ([A-Za-z0-9_]+)\}"))
            {
                if (!keys.Contains(m.Groups[1].Value)) continue;
                stray++;
                if (strayWhere.Count < 6) strayWhere.Add(Path.GetFileName(f) + " → " + m.Groups[1].Value);
            }
        }
        A24($"没有静态引用调色板 key（否则换肤不生效）", stray == 0);
        foreach (var s2 in strayWhere) W("     " + s2);

        W($"  第 24 节小结: {s24pass} PASS / {s24fail} FAIL");
    }

}
catch (Exception ex)
{
    W("!! 探针异常: " + ex);
}

File.WriteAllText(outFile, sb.ToString(), new UTF8Encoding(false));
Console.WriteLine();
Console.WriteLine("结果已写入: " + outFile);
Console.WriteLine("ALL DONE");
