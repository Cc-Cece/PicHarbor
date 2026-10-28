// PicHarbor Website JavaScript (Bilingual, Theme, Tabs, Release Loader)

const I18N = {
  en: {
    nav_features: "Features",
    nav_safety: "Safety",
    nav_showcase: "Showcase",
    nav_download: "Download",
    nav_docs: "Docs",

    hero_badge: "Open Source • Local-First Photo Harbor",
    hero_title_1: "Your iPhone & Android Photos,",
    hero_title_2: "Safely Backed Up.",
    hero_subtitle: "Lossless local photo & video backup for iPhone (AFC USB) and Android (FTP). Keep original quality, browse offline, and sync seamlessly.",
    btn_download_gui: "Download Desktop GUI",
    btn_qq_group: "Join QQ Group",
    btn_github: "GitHub",
    hero_notice_tag: "Beta",
    hero_notice_text: "iPhone & Android mobile apps are now in beta test. Join the QQ group to get access.",

    sec_features_tag: "Core Capabilities",
    sec_features_title: "Engineered for Reliability & Data Safety",
    sec_features_sub: "No cloud lock-in, no lossy compression, and no accidental deletion. Built on high-performance .NET 10.",

    feat_1_title: "Lossless iPhone USB Archiving",
    feat_1_desc: "Direct Apple File Conduit (AFC) protocol over USB. Eliminates MTP crashes and corrupt files while fully preserving Live Photos and RAW files.",

    feat_2_title: "Android FTP Wireless Backup",
    feat_2_desc: "Connect over local Wi-Fi via FTP to back up original photos and videos. Supports multi-device identification and smart incremental backups.",

    feat_3_title: "Offline SQLite Index & Search",
    feat_3_desc: "Instant offline search without connecting devices. Filter by date, camera model, media type, or GPS coordinates with gallery lightbox.",

    feat_4_title: "Restore & Sync to iPhone",
    feat_4_desc: "Export organized sync albums back into iOS Photos via iTunes or Apple Devices, keeping Live Photo motion pairs completely intact.",

    feat_5_title: "Android Incremental Sync",
    feat_5_desc: "High-speed FTP synchronization to Android devices. Smart journal skips already archived media to prevent duplicate transfers.",

    feat_6_title: "Google Photos Cloud Backup",
    feat_6_desc: "Integrated gpmc bridge supporting Pixel original quality (unlimited quota) or Storage Saver, with automatic retry and album grouping.",

    sec_showcase_tag: "Showcase",
    sec_showcase_title: "Simple, Fast, and Controllable",
    sec_showcase_sub: "Modern desktop app and scriptable tools sharing the same lossless local SQLite database.",

    tab_btn_iphone: "📥 iPhone USB",
    tab_btn_android: "🤖 Android FTP",
    tab_btn_restore: "🔄 Multi-Device Sync",
    tab_btn_gphotos: "☁️ Google Photos",
    tab_btn_cli: "⚡ Terminal CLI",

    tab_iphone_h3: "Native AFC High-Speed Archiving",
    tab_iphone_p: "Bypasses flaky Windows MTP. Employs atomic write pipelines and SQLite journaling so unplugging mid-copy safely resumes upon reconnect.",
    tab_iphone_li1: "Direct AFC protocol via Apple usbmuxd service",
    tab_iphone_li2: "Flexible scope: full roll, date range, or DCIM folder",
    tab_iphone_li3: "Automatic organization: month, year-month, year, or flat",

    tab_android_h3: "Back Up Android Albums to PC",
    tab_android_p: "Start an FTP server on your phone and connect in seconds. Select target albums for rapid incremental backup to local storage.",
    tab_android_li1: "Quick Wi-Fi connection via phone FTP address & port",
    tab_android_li2: "Album-level selection with device identification",
    tab_android_li3: "Keep a local gallery for offline browsing and later restore",
    tab_android_example: "Android Connection Steps",
    tab_android_example_1: "1. Start FTP server on phone (same local network)",
    tab_android_example_2: "2. Enter IP & credentials in PicHarbor GUI",
    tab_android_example_3: "3. Choose albums and local destination directory",
    tab_android_example_4: "4. Click to start incremental backup",

    tab_restore_h3: "Cross-Device Migration & Restore",
    tab_restore_p: "Effortlessly send archived photos to new phones. Synchronize back into iOS Photos, or upload directly to Android over FTP.",
    tab_restore_li1: "Retains Live Photo motion pairs and edit sidecars",
    tab_restore_li2: "Auto-pairing with device identifier",
    tab_restore_li3: "Smart incremental skip using local journal",

    tab_gphotos_h3: "Google Photos Cloud Integration",
    tab_gphotos_p: "Integrated gpmc cloud bridge with automatic token capture and support for Pixel unlimited storage tier.",
    tab_gphotos_li1: "Built-in login window to capture OAuth credentials",
    tab_gphotos_li2: "Supports Pixel Original (unlimited) or Storage Saver",
    tab_gphotos_li3: "Multi-threaded upload with automatic rate backoff",

    tab_cli_h3: "Scriptable CLI with Live Dashboard",
    tab_cli_p: "Designed for automation, headless tasks, or power users. Features real-time terminal progress indicators.",
    tab_cli_li1: "Atomic resume on Ctrl+C or disconnect",
    tab_cli_li2: "Dry-run simulation mode (--dry-run)",
    tab_cli_li3: "Offline lossless archive reorganizer",

    sec_safety_tag: "Safety by Design",
    sec_safety_title: "Keep Originals. Verify Your Backups.",
    sec_safety_sub: "Read-only architecture and atomic write pipelines ensure phone photos stay untouched and local backups stay intact.",
    safety_1_title: "Strict Read-Only Contract",
    safety_1_desc: "The core engine strictly reads media and does not import or execute any write/delete commands against connected devices.",
    safety_2_title: "Atomic Write Pipeline",
    safety_2_desc: "Transfers write to temporary files and verify byte integrity before atomic rename, preventing 0-byte corrupt files.",
    safety_3_title: "Disconnect Watchdog",
    safety_3_desc: "Active heartbeat monitor safely records completed progress if cable unplugs, smoothly resuming upon reconnection.",

    sec_download_tag: "Get Started",
    sec_download_title: "Download PicHarbor",
    sec_download_sub: "Portable standalone Windows app and mobile beta recruitment.",

    dl_gui_title: "PicHarbor Desktop (GUI)",
    dl_gui_badge: "Recommended",
    dl_gui_desc: "All-in-one desktop app to back up iPhone and Android, browse offline libraries, and restore across devices.",
    dl_gui_os: "Windows 10/11 (x64)",
    dl_gui_type: "Portable Single-File EXE",
    dl_gui_btn: "Download PicHarbor.Gui.exe",

    dl_mobile_title: "PicHarbor Mobile App",
    dl_mobile_badge: "Beta Testing",
    dl_mobile_desc: "iPhone & Android mobile clients are currently in closed beta test! Join our official QQ group to get beta builds.",
    dl_mobile_platform_label: "Platform",
    dl_mobile_platform_val: "iOS & Android",
    dl_mobile_stage_label: "Status",
    dl_mobile_stage_val: "Public Beta",
    dl_mobile_channel_label: "Access",
    dl_mobile_channel_val: "QQ Group",
    dl_mobile_price_label: "License",
    dl_mobile_price_val: "Free & Open Source",
    dl_mobile_btn: "Join QQ Group",

    cli_alt_note: "Need automation or headless scripts? Also available:",
    cli_alt_link: "Download PicHarbor CLI (Terminal Tool)",

    release_notes_label: "View changelog and SHA-256 verification on",
    release_notes_link: "GitHub Releases",

    steps_title: "Quick 3-Step Setup",
    step_1_title: "1. Prepare Phone",
    step_1_desc: "iPhone: connect via USB and tap Trust. Android: turn on phone FTP server.",
    step_2_title: "2. Connect in App",
    step_2_desc: "Open PicHarbor.Gui.exe and select your device to verify connection.",
    step_3_title: "3. Start Backup",
    step_3_desc: "Choose photo albums and local directory, then start lossless backup.",

    footer_desc: "A local-first, lossless photo management and backup tool for Windows. Protecting your irreplaceable memories with engineering rigor.",
    footer_links_title: "Links",
    footer_resources_title: "Resources",
    footer_manifest: "Manifest Schema",
    footer_troubleshoot: "Troubleshooting",
    footer_license: "GNU GPLv3 License",
    footer_attrib: "Acknowledgements: Based on ideas from get-and-see (MIT), gpmc (MIT), and PCL (other).",
    footer_author_notice: "The author opposes using this project to drive traffic or for marketing. Please do not impersonate the official project or falsely claim the author's endorsement.",
    footer_copy: "PicHarbor Open Source Project. Released under GNU GPLv3."
  },
  zh: {
    nav_features: "功能特性",
    nav_safety: "安全保证",
    nav_showcase: "界面演示",
    nav_download: "下载软件",
    nav_docs: "参考文档",

    hero_badge: "开源项目 • 本地化照片安全避风港",
    hero_title_1: "iPhone 与 Android 相册",
    hero_title_2: "本地无损备份与管理",
    hero_subtitle: "iPhone 通过 USB（AFC）、Android 通过 FTP，将原始照片视频无损备份至本地电脑。支持离线图册浏览与跨设备同步。",
    btn_download_gui: "下载桌面客户端 (GUI)",
    btn_qq_group: "加入 QQ 群",
    btn_github: "访问 GitHub 仓库",
    hero_notice_tag: "移动端内测",
    hero_notice_text: "已支持 iPhone 端与安卓端内测，加群即可获取体验",

    sec_features_tag: "核心功能",
    sec_features_title: "为数据安全与绝对可靠性而设计",
    sec_features_sub: "无云端厂商锁定、无有损画质压缩、杜绝意外误删。基于高性能 .NET 10 构建。",

    feat_1_title: "iPhone USB 无损直接归档",
    feat_1_desc: "直连原生 AFC 协议，规避 Windows MTP 断连与 0 字节损坏文件，完整保留 Live Photo、RAW 与编辑关联。",

    feat_2_title: "Android FTP 无线极速备份",
    feat_2_desc: "局域网无线连接手机，选择相册极速备份原始照片视频，支持多设备标识与智能增量。",

    feat_3_title: "SQLite 离线即时检索与画廊",
    feat_3_desc: "无需插手机即可离线秒级检索。支持时间、机型、类型与 GPS 多维筛选，内置大图与视频播放。",

    feat_4_title: "一键导出恢复至 iPhone",
    feat_4_desc: "一键生成标准同步相册，配合 iTunes / Apple Devices 导回 iOS 相册，完美保留实况照片动态效果。",

    feat_5_title: "Android 极速恢复与增量同步",
    feat_5_desc: "通过 FTP 极速上传媒体至 Android 设备，基于数据库智能跳过已有文件，避免重复传输。",

    feat_6_title: "Google 相册云端备份集成",
    feat_6_desc: "集成 gpmc 桥接工具，支持 Pixel 原画（不计配额）与节省空间画质，自动按年月归类并支持重试。",

    sec_showcase_tag: "界面演示",
    sec_showcase_title: "简单、极速、一切尽在掌控",
    sec_showcase_sub: "功能完备的现代化桌面客户端与高效命令行工具，共享同一本地无损数据库。",

    tab_btn_iphone: "📥 iPhone USB 归档",
    tab_btn_android: "🤖 Android FTP 备份",
    tab_btn_restore: "🔄 跨设备同步恢复",
    tab_btn_gphotos: "☁️ Google 相册",
    tab_btn_cli: "⚡ 命令行 CLI",

    tab_iphone_h3: "真正的原生 AFC 高速无损归档",
    tab_iphone_p: "摆脱 Windows 资源管理器 MTP 协议的卡死与断流。基于原子写入机制，中途拔线重新连接自动无缝续传。",
    tab_iphone_li1: "通过 usbmuxd 服务直连原生 AFC 协议",
    tab_iphone_li2: "灵活范围：全量相机胶卷、日期区间或 DCIM 子目录",
    tab_iphone_li3: "多种目录结构：月份、年月层级、年份或平铺",

    tab_android_h3: "将 Android 相册备份到电脑",
    tab_android_p: "手机开启 FTP 服务，客户端秒级连接，选择相册即可将原始照片视频增量备份至电脑本地。",
    tab_android_li1: "局域网直连手机 FTP 地址与端口",
    tab_android_li2: "按相册选择范围，支持多设备历史识别",
    tab_android_li3: "备份后可离线浏览本地图册，支持随时恢复",
    tab_android_example: "Android 连接步骤",
    tab_android_example_1: "1. 手机在同一 Wi-Fi 下开启 FTP 服务",
    tab_android_example_2: "2. 在客户端填入地址并点击连接",
    tab_android_example_3: "3. 勾选要备份的相册与本地保存目录",
    tab_android_example_4: "4. 开始增量备份，在任务中心查看进度",

    tab_restore_h3: "多端跨设备同步与恢复",
    tab_restore_p: "轻松将备份的照片导出至新手机。既可借助 iTunes / Apple Devices 导回 iPhone，亦可通过局域网 FTP 极速同步至 Android 设备。",
    tab_restore_li1: "完整保留实况照片（Live Photo）与编辑记录",
    tab_restore_li2: "Android 端自动配对设备标识",
    tab_restore_li3: "基于数据库记录的智能增量跳过，不重复传输",

    tab_gphotos_h3: "Google 相册云端备份无缝衔接",
    tab_gphotos_p: "集成 gpmc 云端桥接工具，提供内置窗口登录认证，完整支持 Pixel 专属不计配额画质上传。",
    tab_gphotos_li1: "内置窗口自动捕获登录凭据",
    tab_gphotos_li2: "支持 Pixel 原画质（无限配额）或节省空间画质",
    tab_gphotos_li3: "多线程并发上传与网络抖动自动退避重试",

    tab_cli_h3: "可脚本化调用的命令行与实时看板",
    tab_cli_p: "专为自动化脚本、定时任务或进阶用户设计。内置美观的终端交互式实时进度看板。",
    tab_cli_li1: "Ctrl+C 或断开拔线后安全保存，可直接续传",
    tab_cli_li2: "支持仅扫描计划不实际写入的预览模式 (--dry-run)",
    tab_cli_li3: "支持电脑本地零拷贝目录重整 (reorganize)",

    sec_safety_tag: "安全设计理念",
    sec_safety_title: "保留原始文件，核验本地备份",
    sec_safety_sub: "iPhone AFC 备份仅读取相机胶卷，不修改其中的原始文件。结合传输记录与完整性校验，守护每一张照片。",
    safety_1_title: "架构级只读合约保证",
    safety_1_desc: "核心底层代码严格遵循只读协议，绝不引用、不执行任何修改或写入手机存储的指令。",
    safety_2_title: "原子写入与完整性校验",
    safety_2_desc: "文件先写入 .partial 临时文件，严格核验字节大小无误后原子重命名到位，绝无半截损坏文件。",
    safety_3_title: "断连看门狗机制",
    safety_3_desc: "毫秒级心跳检测，中途拔出手机或进入休眠时自动保存进度，重新连接后平滑断点续传。",

    sec_download_tag: "即刻体验",
    sec_download_title: "获取 PicHarbor",
    sec_download_sub: "适用于 Windows 10/11 的独立单文件桌面版，及全新移动端内测。",

    dl_gui_title: "PicHarbor 桌面客户端",
    dl_gui_badge: "推荐使用",
    dl_gui_desc: "在同一个桌面应用中无损备份 iPhone 与 Android、离线浏览本地图册、跨设备恢复，并支持云端同步。",
    dl_gui_os: "Windows 10/11 (x64)",
    dl_gui_type: "独立单文件便携版 (EXE)",
    dl_gui_btn: "下载 PicHarbor.Gui.exe",

    dl_mobile_title: "PicHarbor 移动端",
    dl_mobile_badge: "内测招募",
    dl_mobile_desc: "已支持 iPhone 端与安卓端内测！移动端支持本地极速备份与电脑无线互传，加群获取测试版与安装指引。",
    dl_mobile_platform_label: "支持平台",
    dl_mobile_platform_val: "iOS & Android",
    dl_mobile_stage_label: "当前状态",
    dl_mobile_stage_val: "公开内测中",
    dl_mobile_channel_label: "获取方式",
    dl_mobile_channel_val: "QQ 交流群",
    dl_mobile_price_label: "开源许可",
    dl_mobile_price_val: "完全免费 / 遵循开源",
    dl_mobile_btn: "加入 QQ 群",

    cli_alt_note: "需要脚本自动化或静默运行？亦提供：",
    cli_alt_link: "下载 PicHarbor 命令行工具 (CLI)",

    release_notes_label: "查看版本详细更新日志与 SHA-256 校验和：",
    release_notes_link: "GitHub Releases 页面",

    steps_title: "快速上手三步法",
    step_1_title: "1. 准备手机连接",
    step_1_desc: "iPhone 用 USB 连接电脑并解锁信任；Android 在同一 Wi-Fi 下开启 FTP 服务。",
    step_2_title: "2. 在客户端中连接",
    step_2_desc: "打开 PicHarbor.Gui.exe，选择对应设备类型并完成一键连接测试。",
    step_3_title: "3. 勾选相册，开始备份",
    step_3_desc: "选择目标相册与电脑存储目录，点击即可开始无损增量归档。",

    footer_desc: "一款面向 Windows 的本地化、无损照片管理与备份工具。以严苛的工程实践守护每一份无可替代的珍贵回忆。",
    footer_links_title: "相关链接",
    footer_resources_title: "参考资料",
    footer_manifest: "数据库清单说明",
    footer_troubleshoot: "故障排除与常见问题",
    footer_license: "GNU GPLv3 开源协议",
    footer_attrib: "开源致谢：部分思路与组件参考自 get-and-see (MIT) 、 gpmc (MIT) 与 PCL (other)。",
    footer_author_notice: "作者反对利用本项目引流、营销。请勿冒充官方或虚称作者背书。",
    footer_copy: "PicHarbor 开源项目。采用 GNU GPLv3 协议开源。"
  }
};

let currentLang = "en";

function setLanguage(lang) {
  if (!I18N[lang]) return;
  currentLang = lang;
  document.documentElement.lang = lang === "zh" ? "zh-CN" : "en";
  localStorage.setItem("picharbor-lang", lang);

  const dict = I18N[lang];
  document.querySelectorAll("[data-i18n]").forEach(el => {
    const key = el.getAttribute("data-i18n");
    if (dict[key]) {
      el.textContent = dict[key];
    }
  });

  const langToggleBtn = document.getElementById("langToggle");
  if (langToggleBtn) {
    langToggleBtn.textContent = lang === "zh" ? "English" : "中文";
  }
}

function initLanguage() {
  const saved = localStorage.getItem("picharbor-lang");
  if (saved && I18N[saved]) {
    setLanguage(saved);
  } else {
    const navLang = navigator.language || navigator.userLanguage || "";
    if (navLang.startsWith("zh")) {
      setLanguage("zh");
    } else {
      setLanguage("en");
    }
  }

  const langToggleBtn = document.getElementById("langToggle");
  if (langToggleBtn) {
    langToggleBtn.addEventListener("click", () => {
      setLanguage(currentLang === "en" ? "zh" : "en");
    });
  }
}

function initTheme() {
  const themeToggleBtn = document.getElementById("themeToggle");
  const savedTheme = localStorage.getItem("picharbor-theme");
  const prefersDark = window.matchMedia("(prefers-color-scheme: dark)").matches;
  
  const activeTheme = savedTheme || (prefersDark ? "dark" : "light");
  document.documentElement.setAttribute("data-theme", activeTheme);
  updateThemeIcon(activeTheme);

  if (themeToggleBtn) {
    themeToggleBtn.addEventListener("click", () => {
      const current = document.documentElement.getAttribute("data-theme");
      const next = current === "light" ? "dark" : "light";
      document.documentElement.setAttribute("data-theme", next);
      localStorage.setItem("picharbor-theme", next);
      updateThemeIcon(next);
    });
  }
}

function updateThemeIcon(theme) {
  const icon = document.getElementById("themeIcon");
  if (!icon) return;
  if (theme === "light") {
    icon.innerHTML = `<svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="5"></circle><line x1="12" y1="1" x2="12" y2="3"></line><line x1="12" y1="21" x2="12" y2="23"></line><line x1="4.22" y1="4.22" x2="5.64" y2="5.64"></line><line x1="18.36" y1="18.36" x2="19.78" y2="19.78"></line><line x1="1" y1="12" x2="3" y2="12"></line><line x1="21" y1="12" x2="23" y2="12"></line><line x1="4.22" y1="19.78" x2="5.64" y2="18.36"></line><line x1="18.36" y1="5.64" x2="19.78" y2="4.22"></line></svg>`;
  } else {
    icon.innerHTML = `<svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M21 12.79A9 9 0 1 1 11.21 3 7 7 0 0 0 21 12.79z"></path></svg>`;
  }
}

function initTabs() {
  const tabs = document.querySelectorAll(".tab-btn");
  tabs.forEach(tab => {
    tab.addEventListener("click", () => {
      const targetId = tab.getAttribute("data-tab");
      document.querySelectorAll(".tab-btn").forEach(t => t.classList.remove("active"));
      document.querySelectorAll(".tab-content").forEach(c => c.classList.remove("active"));

      tab.classList.add("active");
      const targetContent = document.getElementById(targetId);
      if (targetContent) {
        targetContent.classList.add("active");
      }
    });
  });
}

function initCopyButtons() {
  document.querySelectorAll(".btn-copy").forEach(btn => {
    btn.addEventListener("click", async () => {
      const targetText = btn.getAttribute("data-copy");
      if (!targetText) return;

      try {
        await navigator.clipboard.writeText(targetText);
        const originalText = btn.textContent;
        btn.textContent = I18N[currentLang].cmd_copied || "Copied!";
        btn.style.background = "#10b981";
        btn.style.color = "#ffffff";
        setTimeout(() => {
          btn.textContent = originalText;
          btn.style.background = "";
          btn.style.color = "";
        }, 2000);
      } catch (err) {
        console.error("Copy failed", err);
      }
    });
  });
}

async function loadLatestRelease() {
  const repoNames = ["Cc-Cece/get-and-see", "Cc-Cece/PicHarbor"];
  let releaseData = null;

  for (const repo of repoNames) {
    try {
      const res = await fetch(`https://api.github.com/repos/${repo}/releases/latest`, {
        headers: { "Accept": "application/vnd.github.v3+json" }
      });
      if (res.ok) {
        releaseData = await res.json();
        break;
      }
    } catch (e) {
      // Ignore and fallback
    }
  }

  // Fallback to local version.json if github api is rate-limited or offline
  if (!releaseData) {
    try {
      const localRes = await fetch("version.json");
      if (localRes.ok) {
        const localData = await localRes.json();
        updateReleaseDom({
          tag_name: localData.version || "v1.0.0",
          html_url: localData.release_url || "https://github.com/Cc-Cece/PicHarbor/releases",
          assets: []
        });
        return;
      }
    } catch (e) {}
  }

  if (releaseData) {
    updateReleaseDom(releaseData);
  }
}

function updateReleaseDom(release) {
  const tagEls = document.querySelectorAll(".release-tag");
  tagEls.forEach(el => el.textContent = release.tag_name || "v1.0.0");

  const releaseLinkEl = document.getElementById("releaseLink");
  if (releaseLinkEl && release.html_url) {
    releaseLinkEl.href = release.html_url;
  }

  if (release.assets && release.assets.length > 0) {
    const guiAsset = release.assets.find(a => a.name.toLowerCase().includes("gui"));
    const cliAsset = release.assets.find(a => a.name.toLowerCase() === "picharbor.exe" || a.name.toLowerCase().includes("cli") || a.name.toLowerCase() === "get-and-see.exe");

    const guiBtn = document.getElementById("guiDownloadBtn");
    if (guiBtn && guiAsset) {
      guiBtn.href = guiAsset.browser_download_url;
      const sizeMb = (guiAsset.size / (1024 * 1024)).toFixed(1);
      const sizeEl = document.getElementById("guiFileSize");
      if (sizeEl) sizeEl.textContent = `~${sizeMb} MB`;
    }

    const cliBtn = document.getElementById("cliDownloadBtn");
    if (cliBtn && cliAsset) {
      cliBtn.href = cliAsset.browser_download_url;
      const sizeMb = (cliAsset.size / (1024 * 1024)).toFixed(1);
      const sizeEl = document.getElementById("cliFileSize");
      if (sizeEl) sizeEl.textContent = `~${sizeMb} MB`;
    }
  }
}

document.addEventListener("DOMContentLoaded", () => {
  initLanguage();
  initTheme();
  initTabs();
  initCopyButtons();
  loadLatestRelease();
});
