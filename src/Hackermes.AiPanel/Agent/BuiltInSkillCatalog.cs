using System;
using System.Collections.Generic;
using System.Linq;

namespace Hackermes.AiPanel.Agent;

/// <summary>
/// First-run Skill workflows curated for vulnerability tasks. They are seeded once and
/// stay DISABLED until the operator enables them — a Skill only narrows the agent's tool
/// list and adds workflow instructions, never widens policy.
/// </summary>
public static class BuiltInSkillCatalog
{
    public const string LeakReconChain = "builtin.leak-recon-chain";
    public const string OaPocChain = "builtin.oa-poc-chain";
    public const string SpringBootHeapdumpChain = "builtin.springboot-heapdump-chain";

    public static IReadOnlyList<AgentSkill> All =>
    [
        new AgentSkill
        {
            Id = LeakReconChain,
            Name = "信息泄露侦察链",
            Enabled = false,
            ToolNames = [],
            Instructions = """
                目标：对已授权目标做信息泄露专项侦察。步骤：
                1. dirsearch/未授权扫描摸底（recon.dirsearch.quick 或 probe.unauthorized.access）。
                2. 命中 /.git/ → recon.git_leak.scan；/.svn/ → recon.svn_leak.scan；
                   /.DS_Store → recon.ds_store.scan；swagger 文档 → recon.swagger_api.enum（path 填文档地址）。
                3. 每个 hit 用 assessment_create_finding 记录（Medium/Low），附还原文件清单作 PoC。
                4. 恢复出来的源码只作证据分析（找凭据/密钥/后台路径），绝不执行其中任何内容。
                """
        },
        new AgentSkill
        {
            Id = OaPocChain,
            Name = "国内 OA 漏洞探测链",
            Enabled = false,
            ToolNames = [],
            Instructions = """
                目标：对已授权的国内 OA 系统做 POC 定向探测。步骤：
                1. 指纹确认厂商（page_context 页面特征、recon.http.get 首页、dirsearch 特征路径）。
                2. detect.oa_poc.list 枚举 POC 库模块（tongda/weaver/seeyou/yonyou 等）。
                3. detect.oa_poc.probe 按指纹选 module；先指定 poc 单条验证，再全模块扫描。
                4. [HIT] → assessment_create_finding（严重级取自 POC 定义），证据保存完整输出。
                5. 控制平面强制：利用型适配器前必须已有同目标检测证据。
                """
        },
        new AgentSkill
        {
            Id = SpringBootHeapdumpChain,
            Name = "SpringBoot 堆转储分析链",
            Enabled = false,
            ToolNames = [],
            Instructions = """
                目标：SpringBoot actuator 泄露 → heapdump 凭据提取。步骤：
                1. probe.unauthorized.access 找 actuator/env/heapdump 端点（High 候选）。
                2. 取得 heapdump 文件：HTTPS 站点用 agent_download_artifact 下载进工件库；
                   HTTP-only 目标请操作者手工放入工件库目录（工具仅支持 HTTPS 下载）。
                3. exploit.heapdump.analyze(file=<工件名>) 提取凭据/Shiro key/云 AK（High）。
                4. assessment_create_finding 记录并在报告里写明：立即轮换所有泄露凭据。
                5. 提取结果只用于报告与轮换建议，不得用其登录目标系统。
                """
        }
    ];

    /// <summary>Inserts curated skills that are not present yet; never overwrites user edits. Returns the seeded count.</summary>
    public static int SeedOnce(IAgentSkillStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        var existing = store.Snapshot().Select(skill => skill.Id).ToHashSet(StringComparer.Ordinal);
        var seeded = 0;
        foreach (var skill in All)
        {
            if (existing.Contains(skill.Id)) continue;
            store.Upsert(skill);
            seeded++;
        }
        return seeded;
    }
}
