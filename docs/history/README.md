# Desktop Hub 历史快照

这些文件来自 Git 初始化之前保留的本地备份，于 2026-10-10 归档。它们是历史源码快照，不代表当时已存在 Git 提交。

| 阶段 | 位置 | 来源与完整性 |
| --- | --- | --- |
| M0 | ../../prototype/ | 独立可运行原型；后续阶段的原型保留检查记录确认未改动 |
| M1 | ../M1_Implementation_Report.md | 保留报告和 ../m1-evidence/ 验收记录，未找到独立源码快照 |
| M1.5 | M1.5/ | M2.1 开始前的完整 src、desktop、tests、scripts、prototype 备份；原始 manifest.json 的 SHA-256 已逐项校验 |
| M2.1 | M2.1/ | M2.2 开始前的 src、desktop、tests、scripts、prototype、README、PRD 备份；原始 manifest.json 的 SHA-256 已逐项校验 |
| M2.2 交互修复前 | M2.2-before-usability-partial/ | 仅保存此次修复涉及的部分文件，不是完整可独立构建的源码快照 |
| 当前 M2.2 | 仓库根目录 | 首次提交 fb1f0a74a887a122524e1d2e1443071316653c06 保存的完整项目 |

M1.5 快照未包含当时 README；阶段说明见 ../M1.5_Implementation_Report.md。阶段报告、通信协议及验收记录保留在 docs 中。

历史快照不包含工具链、bin/obj 或发布程序。归档时只校验备份完整性，没有重新运行旧版本构建和验收。
