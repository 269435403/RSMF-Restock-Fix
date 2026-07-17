---
name: "github-publisher"
description: "Safely publishes this RimWorld mod to its configured GitHub repository and updates repository metadata. Invoke when uploading, syncing, or changing the GitHub description."
---

# GitHub Publisher

用于更新本项目 `RSMF Restock Fix` 的 GitHub 仓库。

## 固定仓库

- Owner: `269435403`
- Repository: `RSMF-Restock-Fix`
- Branch: `main`
- 本地目录：当前 Skill 所在项目根目录

## 发布流程

1. 先检查 `git remote -v`、`git status --short --branch` 和当前分支。
2. 检查是否存在未提交改动；不要把 `.env`、凭据或无关文件加入提交。
3. 发布前确认远端仓库和分支。若远端领先，先执行 `git pull --rebase origin main`，禁止直接强推覆盖远端。
4. 将本项目需要发布的文件按路径精确加入暂存区；通常包括 `About/`、`Assemblies/`、`Languages/`、`Preview/`、`Source/`、`README.md` 和项目配置文件。
5. 使用说明性的提交信息提交，然后执行 `git push origin main`。
6. 若推送出现 `fetch first`，不要重复推送或强制推送；执行 `git pull --rebase origin main` 后再推送。
7. 推送完成后检查 `git status --short --branch`，确认工作区干净且与 `origin/main` 一致。

## MCP 使用规则

调用 GitHub MCP 前，必须先从 MCP 文件系统列出并读取对应工具描述。参数必须全部放在 `run_mcp` 的 `args` 对象内。

- 查询仓库：先读取 `search_repositories.json`，再调用 `mcp_GitHub.search_repositories`。
- 查看远端文件：先读取 `get_file_contents.json`，再调用 `mcp_GitHub.get_file_contents`。
- 修改单个文本文件或仓库元数据：优先使用对应的 GitHub MCP 工具，并先获取现有文件 SHA（如果工具要求）。
- 批量上传：先读取 `push_files.json`，但不要用它上传 DLL、PNG 等大型二进制或大量文件；本项目发布优先使用本地 Git。

## 仓库描述规范

仓库 description 应简洁概括模组用途；创意工坊长描述不要塞进仓库 description。更新仓库 description 前先读取 `create_repository.json` 以确认可用参数；若当前 MCP 没有更新仓库元数据的工具，不要冒险调用不存在的工具，应明确告知用户并使用可行替代方案。

## 常见踩坑

- 远端可能在本地检查后被其他提交更新，导致 `git push` 报 `fetch first`；用 rebase 合并，不要 force push。
- GitHub MCP 的 `push_files` 只接受文本内容，不能可靠处理本项目 DLL、PNG 等二进制文件。
- 工作区可能同时有已修改文件和未跟踪的 Workshop 资源；发布前必须逐项确认，避免遗漏或误上传。
- PackageID 修改不只涉及 `About/About.xml`，还要同步 README 和 Harmony ID 等代码引用；发布前应搜索旧 PackageID。
- GitHub 仓库 description 与 Steam Workshop description 是两个不同字段，不能混用。
