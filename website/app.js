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
    hero_subtitle: "Back up iPhone over USB (AFC) and Android over FTP to your Windows PC. Keep original photos and videos, browse your library offline, and restore or sync to iPhone, Android, and Google Photos.",
    btn_download_gui: "Download GUI (App)",
    btn_download_cli: "Download CLI",
    btn_github: "GitHub",
    cmd_copied: "Copied!",
    cmd_copy: "Copy",
    
    sec_features_tag: "Core Capabilities",
    sec_features_title: "Engineered for Reliability & Data Safety",
    sec_features_sub: "No cloud lock-in, no lossy compression, and no accidental deletion. Built on high-performance .NET 10.",

    feat_1_title: "Lossless iPhone USB Archiving",
    feat_1_desc: "Direct Apple File Conduit (AFC) protocol over USB. Eliminates Windows MTP explorer crashes and 0-byte corrupt transfers. Live Photos, RAW, and AAE sidecars paired seamlessly.",

    feat_2_title: "Android FTP Photo & Video Backup",
    feat_2_desc: "Connect to an FTP server on your Android phone, select albums, and back up original photos and videos to your PC. Device identification and incremental backups keep each phone's archive organized.",

    feat_3_title: "Offline SQLite Index & Search",
    feat_3_desc: "Browse archives and search media instantly without reconnecting devices. Query by date, camera model, media type, file size, or GPS coordinates with gallery lightbox preview.",

    feat_4_title: "Restore & Sync to iPhone",
    feat_4_desc: "Generates structured sync albums (flat or YYYY-MM) for Apple Devices or iTunes to synchronize back into iOS Photos, keeping Live Photo video/photo links completely intact.",

    feat_5_title: "Restore & Incremental Android Sync",
    feat_5_desc: "Direct FTP synchronization to Android or Pixel devices. Supports full restore or incremental skip based on SQLite historical records to avoid repeated transfers.",

    feat_6_title: "Google Photos Cloud Backup",
    feat_6_desc: "Integrated gpmc cloud bridge with automatic OAuth token capture, Pixel unlimited storage or Storage Saver quality, auto-albums, and retry with jitter backoff.",

    sec_showcase_tag: "Dual Interfaces",
    sec_showcase_title: "Choose the Way You Work",
    sec_showcase_sub: "A rich modern desktop application and a powerful scriptable command-line tool, sharing the same archive format.",

    tab_btn_iphone: "📥 iPhone USB Archiving",
    tab_btn_android: "🤖 Android FTP Backup",
    tab_btn_restore: "🔄 Multi-Device Sync",
    tab_btn_gphotos: "☁️ Google Photos",
    tab_btn_cli: "⚡ Terminal CLI",

    tab_iphone_h3: "True Native AFC Archiving",
    tab_iphone_p: "Bypasses flaky Windows MTP. Employs SQLite journaling and atomic writes (.partial → size verification → atomic rename). Transfers cleanly resume after disconnects.",
    tab_iphone_li1: "Direct AFC protocol via usbmuxd service",
    tab_iphone_li2: "Flexible scope: full roll, date range, or DCIM folder",
    tab_iphone_li3: "Automatic organization: month, year-month, year, or flat",

    tab_android_h3: "Back Up Android Albums to Your PC",
    tab_android_p: "Start an FTP server on your Android phone and connect from the desktop app. Select the albums you want to archive, then run an incremental backup of the original photos and videos.",
    tab_android_li1: "Connect using the phone's FTP address, port, and credentials",
    tab_android_li2: "Choose albums and identify devices across backup sessions",
    tab_android_li3: "Keep a local library for offline browsing and later restore",
    tab_android_example: "Android connection example",
    tab_android_example_1: "1. Start the phone's FTP server on a trusted local network",
    tab_android_example_2: "2. Enter the server address and credentials in PicHarbor",
    tab_android_example_3: "3. Select albums and a local backup folder",
    tab_android_example_4: "4. Start incremental backup; review progress in Task Center",

    tab_restore_h3: "Multi-Destination Cross-Device Restore",
    tab_restore_p: "Effortlessly send archived photos to new phones. Synchronize back into iOS Photos via iTunes, or upload directly to Android via FTP.",
    tab_restore_li1: "Retains Live Photo motion pairs and edit sidecars",
    tab_restore_li2: "Android FTP auto-pairing with .picharbor-device-id",
    tab_restore_li3: "Smart incremental skip using local journal",

    tab_gphotos_h3: "Google Photos Cloud Integration",
    tab_gphotos_p: "Integrated gpmc bridge enables cloud synchronization with support for Pixel unlimited storage tier credentials.",
    tab_gphotos_li1: "OAuth browser capture or GmsCore credentials",
    tab_gphotos_li2: "Supports Original Quality or Storage Saver",
    tab_gphotos_li3: "Multi-threaded upload with automatic rate backoff",

    tab_cli_h3: "Scriptable Command Line with Live Dashboard",
    tab_cli_p: "Designed for automation, headless tasks, or power users. Features a real-time interactive terminal status dashboard.",
    tab_cli_li1: "Atomic resume on Ctrl+C or unplug",
    tab_cli_li2: "Dry-run simulation mode (--dry-run)",
    tab_cli_li3: "Offline lossless archive reorganizer (reorganize)",

    sec_safety_tag: "Safety by Design",
    sec_safety_title: "Keep Originals. Verify Your Backups.",
    sec_safety_sub: "iPhone AFC backups read from the camera roll without modifying it. Local archives use transfer records and integrity checks to support reliable backups.",
    safety_1_title: "Architectural Read-Only Contract",
    safety_1_desc: "Enforced at build time via Mono.Cecil byte-scanning unit tests. The core engine does not import or call any device-write API.",
    safety_2_title: "Atomic Write Pipeline",
    safety_2_desc: "Transfers write to .partial temporary files, verify byte lengths, and rename atomically. Interrupted files never leave corrupt zero-byte artifacts.",
    safety_3_title: "Forward-Progress Watchdog",
    safety_3_desc: "Detects stalled connections and graceful disconnect timeouts. Unplugging mid-copy safely exits and seamlessly resumes on reconnect.",

    sec_download_tag: "Get Started",
    sec_download_title: "Download PicHarbor",
    sec_download_sub: "Portable single-file executables for Windows 10 (19041+) & Windows 11. No installer required.",
    
    dl_gui_title: "PicHarbor GUI",
    dl_gui_badge: "Recommended",
    dl_gui_desc: "Back up iPhone and Android, browse your local media library, restore across devices, and connect to Google Photos in one desktop app.",
    dl_gui_os: "Windows 10/11 (x64)",
    dl_gui_type: "Portable Single-File EXE",
    dl_gui_btn: "Download PicHarbor.Gui.exe",

    dl_cli_title: "PicHarbor CLI",
    dl_cli_badge: "Terminal / Scripts",
    dl_cli_desc: "High-speed CLI with interactive dashboard for automated backups and scheduled tasks.",
    dl_cli_os: "Windows 10/11 (x64)",
    dl_cli_type: "Portable Single-File EXE",
    dl_cli_btn: "Download picharbor.exe",

    release_notes_label: "View changelog and SHA-256 verification on",
    release_notes_link: "GitHub Releases",

    steps_title: "Quick 3-Step Setup",
    step_1_title: "1. Prepare Your Phone",
    step_1_desc: "iPhone: install Apple Devices or iTunes on your PC. Android: start an FTP server on your phone and note its address and credentials.",
    step_2_title: "2. Connect in PicHarbor",
    step_2_desc: "iPhone: connect by USB, unlock, and trust the computer. Android: enter the FTP connection details and test the connection.",
    step_3_title: "3. Choose & Back Up",
    step_3_desc: "Open PicHarbor.Gui.exe, select iPhone or Android on the Backup page, choose your media and destination, and start the backup.",

    footer_desc: "A local-first, lossless photo management and backup tool for Windows. Protecting your irreplaceable memories with engineering rigor.",
    footer_links_title: "Links",
    footer_resources_title: "Resources",
    footer_manifest: "Manifest Schema",
    footer_troubleshoot: "Troubleshooting",
    footer_license: "GNU GPLv3 License",
    footer_attrib: "Acknowledgements: Based on ideas from get-and-see (MIT) & gpmc (MIT).",
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
    hero_subtitle: "iPhone 通过 USB（AFC）、Android 通过 FTP，将原始照片与视频备份到 Windows 电脑。支持本地图册离线浏览、增量备份，以及恢复或同步至 iPhone、Android 和 Google 相册。",
    btn_download_gui: "下载桌面客户端 (GUI)",
    btn_download_cli: "下载命令行 (CLI)",
    btn_github: "访问 GitHub 仓库",
    cmd_copied: "已复制！",
    cmd_copy: "复制",

    sec_features_tag: "核心功能",
    sec_features_title: "为数据安全与绝对可靠性而设计",
    sec_features_sub: "无云端厂商锁定、无有损画质压缩、杜绝意外误删。基于高性能 .NET 10 构建。",

    feat_1_title: "iPhone USB 无损直接归档",
    feat_1_desc: "通过原生 Apple File Conduit (AFC) 协议与 iOS 设备直连，规避 Windows MTP / 资源管理器常见的传输断连与 0 字节损坏文件，完整保留 Live Photo、RAW 与 AAE 关联。",

    feat_2_title: "Android FTP 照片与视频备份",
    feat_2_desc: "连接手机端 FTP 服务，选择相册，将原始照片与视频备份到电脑。支持设备标识与增量备份，方便持续整理不同手机的本地归档。",

    feat_3_title: "SQLite 离线即时检索与画廊",
    feat_3_desc: "无需连接手机即可通过本地数据库瞬间检索。支持按拍摄时间、设备型号、媒体类型、GPS 地理坐标等多维度筛选，并支持大图灯箱与视频播放。",

    feat_4_title: "一键导出恢复至 iPhone",
    feat_4_desc: "一键导出并按规则整理为同步相册（平铺或 YYYY-MM），配合 Apple Devices 或 iTunes 同步回 iOS「照片」，实况照片（Live Photo）视音频对完整保留。",

    feat_5_title: "Android / Pixel FTP 恢复与增量同步",
    feat_5_desc: "通过 FTP 协议直接上传归档媒体至 Android / Pixel 设备，支持常规恢复以及基于 SQLite 历史记录的增量跳过，避免重复上传。",

    feat_6_title: "Google 相册云端备份集成",
    feat_6_desc: "集成 gpmc 桥接工具。支持内置登录捕获 OAuth 凭据，支持 Pixel 原始画质（不计空间配额）或节省空间画质、自动相册归类及异常重试退避。",

    sec_showcase_tag: "双端体验",
    sec_showcase_title: "选择适合你的操作方式",
    sec_showcase_sub: "功能完备的现代化桌面客户端与高效可脚本化的命令行工具，共享同一归档格式与数据库。",

    tab_btn_iphone: "📥 iPhone USB 归档",
    tab_btn_android: "🤖 Android FTP 备份",
    tab_btn_restore: "🔄 跨设备同步恢复",
    tab_btn_gphotos: "☁️ Google 相册",
    tab_btn_cli: "⚡ 命令行 CLI",

    tab_iphone_h3: "真正的原生 AFC 高速无损归档",
    tab_iphone_p: "摆脱 Windows 资源管理器 MTP 协议的卡死与断流。基于 SQLite 日志与原子写入机制（.partial 写入 → 大小校验 → 命名就位），中途断开连接重新运行无缝续传。",
    tab_iphone_li1: "通过 usbmuxd 服务直连原生 AFC 协议",
    tab_iphone_li2: "灵活范围：全量相机胶卷、日期区间或 DCIM 子目录",
    tab_iphone_li3: "多种目录结构：月份、年月层级、年份或平铺",

    tab_android_h3: "将 Android 相册备份到电脑",
    tab_android_p: "在 Android 手机上开启 FTP 服务，通过桌面客户端连接，选择要归档的相册，即可将原始照片与视频增量备份到电脑。",
    tab_android_li1: "填写手机端 FTP 地址、端口与登录凭据",
    tab_android_li2: "按相册选择范围，通过设备标识识别历史备份",
    tab_android_li3: "备份后可离线浏览本地图册，也可用于后续恢复",
    tab_android_example: "Android 连接示例",
    tab_android_example_1: "1. 在可信的本地网络中开启手机 FTP 服务",
    tab_android_example_2: "2. 在 PicHarbor 中填写服务地址和登录凭据",
    tab_android_example_3: "3. 选择相册及电脑上的备份目录",
    tab_android_example_4: "4. 开始增量备份，在任务中心查看进度",

    tab_restore_h3: "多端跨设备同步与恢复",
    tab_restore_p: "轻松将备份的照片导出至新手机。既可借助 iTunes / Apple Devices 导回 iPhone，亦可通过局域网 FTP 极速同步至 Android 设备。",
    tab_restore_li1: "保留实况照片（Live Photo）与编辑修改记录",
    tab_restore_li2: "Android 端通过 .picharbor-device-id 自动配对识别",
    tab_restore_li3: "基于数据库记录的智能增量跳过，不重复传输",

    tab_gphotos_h3: "Google 相册云端备份无缝衔接",
    tab_gphotos_p: "集成 gpmc 云端桥接工具，提供内置窗口登录认证，完整支持 Pixel 专属不计配额画质上传。",
    tab_gphotos_li1: "内置窗口自动捕获 OAuth 凭据或 GmsCore 凭据",
    tab_gphotos_li2: "支持 Pixel 原画质（无限配额）或节省空间画质",
    tab_gphotos_li3: "多线程并发上传与网络抖动自动退避重试",

    tab_cli_h3: "可脚本化调用的命令行与实时看板",
    tab_cli_p: "专为自动化脚本、定时任务或进阶用户设计。内置美观的终端交互式实时进度看板。",
    tab_cli_li1: "Ctrl+C 或断开拔线后自动安全保存，可直接续传",
    tab_cli_li2: "支持仅扫描计划不实际写入的预览模式 (--dry-run)",
    tab_cli_li3: "支持电脑本地零拷贝目录重整 (reorganize)",

    sec_safety_tag: "安全设计理念",
    sec_safety_title: "保留原始文件，核验本地备份",
    sec_safety_sub: "iPhone AFC 备份仅读取相机胶卷，不修改其中的原始文件。本地归档结合传输记录与完整性校验，支持持续、可靠的备份。",
    safety_1_title: "架构级只读合约保证",
    safety_1_desc: "在编译期间通过 Mono.Cecil 进行底层字节码扫描测试。底层核心库不引用、不包含任何修改或写入 iPhone 存储的接口。",
    safety_2_title: "原子写入与完整性校验",
    safety_2_desc: "文件先写入 .partial 临时文件，严格比对字节大小无误后原子重命名到位。绝不会在硬盘上产生 0 字节半截损坏文件。",
    safety_3_title: "断连看门狗机制",
    safety_3_desc: "毫秒级心跳检测与数据停滞看门狗。传输中途拔出手机或进入休眠时，程序自动退出并保存已完成进度，重连后平滑续传。",

    sec_download_tag: "即刻体验",
    sec_download_title: "下载 PicHarbor",
    sec_download_sub: "适用于 Windows 10（19041+）与 Windows 11 的独立单文件便携版，无需安装直接运行。",

    dl_gui_title: "PicHarbor 桌面客户端",
    dl_gui_badge: "推荐普通用户",
    dl_gui_desc: "在同一个桌面客户端中备份 iPhone 与 Android、离线浏览本地图册、跨设备恢复，并连接 Google 相册。",
    dl_gui_os: "Windows 10/11 (x64)",
    dl_gui_type: "独立单文件便携版 (EXE)",
    dl_gui_btn: "下载 PicHarbor.Gui.exe",

    dl_cli_title: "PicHarbor 命令行工具",
    dl_cli_badge: "适合脚本 / 自动化",
    dl_cli_desc: "轻量级高性能命令行程序，具备彩色终端交互进度看板，支持自动化调用与定时任务。",
    dl_cli_os: "Windows 10/11 (x64)",
    dl_cli_type: "独立单文件便携版 (EXE)",
    dl_cli_btn: "下载 picharbor.exe",

    release_notes_label: "查看版本详细更新日志与 SHA-256 校验和：",
    release_notes_link: "GitHub Releases 页面",

    steps_title: "快速开始三步法",
    step_1_title: "1. 准备手机连接",
    step_1_desc: "iPhone：在电脑安装 Apple Devices 或 iTunes。Android：在手机开启 FTP 服务，记下地址和登录凭据。",
    step_2_title: "2. 在 PicHarbor 中连接",
    step_2_desc: "iPhone：通过 USB 连接，解锁并信任电脑。Android：填写 FTP 连接参数并测试连接。",
    step_3_title: "3. 选择范围，开始备份",
    step_3_desc: "打开 PicHarbor.Gui.exe，在备份页选择 iPhone 或 Android，设置媒体范围与目标目录后开始备份。",

    footer_desc: "一款面向 Windows 的本地化、无损照片管理与备份工具。以严苛的工程实践守护每一份无可替代的珍贵回忆。",
    footer_links_title: "相关链接",
    footer_resources_title: "参考资料",
    footer_manifest: "数据库清单说明",
    footer_troubleshoot: "故障排除与常见问题",
    footer_license: "GNU GPLv3 开源协议",
    footer_attrib: "开源致谢：部分思路与组件参考自 get-and-see (MIT) 与 gpmc (MIT)。",
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
    // Sun icon
    icon.innerHTML = `<svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="5"></circle><line x1="12" y1="1" x2="12" y2="3"></line><line x1="12" y1="21" x2="12" y2="23"></line><line x1="4.22" y1="4.22" x2="5.64" y2="5.64"></line><line x1="18.36" y1="18.36" x2="19.78" y2="19.78"></line><line x1="1" y1="12" x2="3" y2="12"></line><line x1="21" y1="12" x2="23" y2="12"></line><line x1="4.22" y1="19.78" x2="5.64" y2="18.36"></line><line x1="18.36" y1="5.64" x2="19.78" y2="4.22"></line></svg>`;
  } else {
    // Moon icon
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
