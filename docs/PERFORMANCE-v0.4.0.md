# Leaf v0.4.0 资源占用实测 / Resource measurements

测量时间：2026-10-06 00:03:27 UTC+8。[原始结构化结果](PERFORMANCE-v0.4.0.json)。

环境：Windows 11 26200，Intel Core i7-11800H（8 核 / 16 逻辑处理器），约 32 GiB RAM，64 位 .NET Framework 4.8，WPF 渲染能力 Tier 2。

运行独立 Leaf 原生托盘进程，使用全新隔离数据、本地延迟 SSE 模拟响应。真实设置、密钥和历史不参与；不发送网络请求，不操作系统剪贴板。每 100 ms 用 Windows 原生计数器采样，待机采样前留 2 秒稳定时间，没有强制 GC 或压缩工作集。

| 阶段 | 时间 (秒) | 平均驻留 (MiB) | 峰值驻留 (MiB) | 平均私有提交 (MiB) | CPU / 全机 (%) | 线程 | 句柄 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 初次托盘待机 | 5.04 | 88.29 | 88.29 | 90.28 | 0.039 | 16 | 542 |
| 打开空浮窗 | 3.06 | 99.24 | 99.24 | 104.63 | 0.000 | 16 | 550 |
| 模拟流式翻译 | 1.94 | 108.79 | 109.32 | 122.28 | 0.454 | 18 | 566 |
| 显示词卡 | 3.07 | 109.82 | 109.82 | 124.73 | 0.032 | 18 | 566 |
| 打开设置 | 3.08 | 113.87 | 113.89 | 143.66 | 0.032 | 19 | 584 |
| 连续开关设置 15 次 | 4.26 | 121.65 | 123.35 | 167.99 | 0.939 | 20 | 587 |
| 使用后回到托盘 | 5.04 | 123.35 | 123.35 | 170.43 | 0.000 | 20 | 587 |

从 Main 入口到托盘就绪约 **492 ms**；全过程驻留峰值 **124.43 MiB**。这是已有系统缓存情况下的启动测量，不是冷启动或冷开机。模拟翻译输出约 840 个汉字，输入约 3000 个字符；其阶段时间包含人为延迟，不能用于判断供应商速度。

## 如何理解这些数字

- **驻留内存 / Working set**：当前物理内存中的映射页面，包含共享框架和系统库。MiB = 1,048,576 字节。
- **私有提交 / Private commit**：进程承诺使用的私有虚拟内存，可能部分在分页文件中；不等于当前驻留，也不等于保留的虚拟地址空间。
- **CPU / 全机**：CPU 时间除以墙钟时间，再除以 16 个逻辑处理器；原始 JSON 另有单核百分比。显示 0 表示本次短测没有可辨别的计数增量，不是永远零占用。
- 任务管理器默认列可能显示私有工作集，因此不会与这里的总驻留值完全一致。GPU 利用率与专用显存没有测量；Tier 2 仅表示 WPF 渲染能力。
- 打开设置再关闭后，CLR/WPF 可能继续保留堆、模板与渲染资源。15 次开关后没有马上回到初始内存，结果如表，不把它解释成已证明无泄漏。短测不代表数小时使用、多屏 DPI、长历史或其他桌面应用的所有情况。

## 其他资源

| 阶段 | GDI 对象 | USER 对象 | 读取字节 | 写入字节 |
| --- | ---: | ---: | ---: | ---: |
| 初次托盘待机 | 31 | 23 | 0 | 0 |
| 打开空浮窗 | 33 | 27 | 0 | 0 |
| 模拟流式翻译 | 34 | 29 | 0 | 1345 |
| 显示词卡 | 34 | 29 | 0 | 0 |
| 打开设置 | 46 | 38 | 0 | 499 |
| 连续开关设置 15 次 | 40 | 34 | 0 | 294 |
| 使用后回到托盘 | 40 | 34 | 0 | 0 |

I/O 是该进程的系统计数器增量，包含其日志和隔离数据写入；不能用来推断磁盘硬件吞吐。日志最多约 5 MiB，另有用户设置与历史。截图和构建源码不装入发布 ZIP。

## 复现 / Reproduce

在源码目录运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/performance.ps1
```

结果位于 `work/performance/run-.../result.json`。测试会短暂打开自己的浮窗和设置，用 Ctrl+Alt+Shift+F12 注册独立快捷键；被占用时报告失败。关闭期间采样，不使用人工干预的 GC 或内存修剪。

English: The tables measure an isolated native tray/WPF process on the listed machine using local delayed SSE fixtures, not a live API. Working set includes shared pages; private commit is a different metric. CPU is normalized over 16 logical processors. Startup is Main-to-ready with warm OS caches. The 15-cycle scenario and retained memory are reported directly; this brief run cannot establish long-term leak absence. GPU usage and real API latency are excluded. Run the script above to repeat the measurement.
