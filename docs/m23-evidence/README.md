# M2.3 验证证据

以 [实施报告](../M2.3_Implementation_Report.md) 的验收范围为准。系统真实写入未执行，不把 Mock 结果当作 Shell/HKCU/登录实测。

- `final-build-tests.txt`：最终 Release 0警告/0错误；Core27、Scene17、Resource27、System Mock23、Lifecycle8、前端62。
- `final-native-tests.txt`、`native/run-20261010T090300051Z/`：最终 M2.3 45项、实际宿主重启、真实热键与前台、截图、只读系统状态与资源观察。
- `m15-regression.txt`、`m15-regression/run-*/`：最终多应用核心扩展30项、原生73项。
- `m21-regression.txt`、`m21-regression/run-*/`：最终场景原生40项。
- `m22-regression.txt`、`m22-regression/run-*/`：最终资源原生42项。
- `development-output.txt`、`development-output-verification.json`：本地开发运行目录更新和文件一致性。
- `frontend-tests.txt`、`lifecycle-tests.txt`：独立复审测试记录。
- `baseline-build.txt`、`integration-build.txt`、`build-tests-attempt1.txt`、较早的 native/run：保留中间失败和随后修复的上下文；不是最终验收结果。

原生记录只包含隔离测试配置/测试创建的资源和无害 fixture。真实用户桌面图标与 HKCU Run 项只读查询。每轮脚本检查新完成标记，归档文件按修改时间限定，旧的 error.txt 不会污染成功轮次。
