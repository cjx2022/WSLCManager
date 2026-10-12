using System;
using System.Linq;
using System.Text;
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

}
catch (Exception ex)
{
    W("!! 探针异常: " + ex);
}

File.WriteAllText(outFile, sb.ToString(), new UTF8Encoding(false));
Console.WriteLine();
Console.WriteLine("结果已写入: " + outFile);
Console.WriteLine("ALL DONE");
