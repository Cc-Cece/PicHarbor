import { SweetAlbum } from './sweet-album.js';

let albumInstance = null;
let currentPhotos = [];

window.addEventListener('error', (e) => {
  postToHost({ action: 'log', message: `JS Error: ${e.message} at ${e.filename}:${e.lineno}` });
});

function postToHost(msg) {
  try {
    if (window.chrome && window.chrome.webview) {
      window.chrome.webview.postMessage(msg);
    } else {
      console.log('[Gallery Host PostMessage]', msg);
    }
  } catch (err) {
    console.error('Failed to post message to host:', err);
  }
}

function updateEmptyState(photosCount) {
  const emptyState = document.getElementById('empty-state');
  const albumContainer = document.getElementById('album-container');
  if (!emptyState || !albumContainer) return;

  if (photosCount === 0) {
    emptyState.style.display = 'block';
    albumContainer.style.display = 'none';
  } else {
    emptyState.style.display = 'none';
    albumContainer.style.display = 'block';
  }
}

let currentPlayingItem = null;

window.setGalleryLoading = function(loading) {
  const scroller = document.querySelector('.sp-scroller') || document.getElementById('album-container');
  const loadingPill = document.getElementById('gallery-loading-pill');
  if (scroller) {
    scroller.style.transition = 'opacity 0.2s ease';
    scroller.style.opacity = loading ? '0.45' : '1.0';
    scroller.style.pointerEvents = loading ? 'none' : 'auto';
  }
  if (loadingPill) {
    loadingPill.style.display = loading ? 'flex' : 'none';
  }
};

function requestNativePreview(item, index) {
  if (!item) return;
  postToHost({
    action: 'openNativePreview',
    index: Number.isInteger(index) ? index : -1,
    path: item.fullPath || '',
    id: item.id || ''
  });
}

function openVideoModal(item) {
  if (!item) return;
  currentPlayingItem = item;
  const modal = document.getElementById('video-modal');
  const video = document.getElementById('html5-video-player');
  const title = document.getElementById('video-title');
  const meta = document.getElementById('video-meta-info');
  const errorBox = document.getElementById('video-error-state');
  if (!modal || !video) return;

  if (errorBox) errorBox.style.display = 'none';
  video.style.display = 'block';

  title.textContent = item.relativePath || item.id || '视频播放';
  meta.textContent = `${(item.format || 'MP4').toUpperCase()} · ${item.takenAt || ''} · ${item.sizeText || ''}`;

  const videoUrl = item.liveVideoUrl || item.url || `https://media.gallery.local/image?path=${encodeURIComponent(item.fullPath)}&v=3`;
  video.src = videoUrl;

  video.onerror = () => {
    console.warn('HTML5 video failed to decode or play, displaying fallback prompt');
    video.style.display = 'none';
    if (errorBox) errorBox.style.display = 'flex';
  };

  modal.style.display = 'flex';
  video.play().catch(e => console.log('Autoplay prevented or paused:', e));
}

function closeVideoModal() {
  const modal = document.getElementById('video-modal');
  const video = document.getElementById('html5-video-player');
  const errorBox = document.getElementById('video-error-state');
  if (errorBox) errorBox.style.display = 'none';
  if (video) {
    video.pause();
    video.onerror = null;
    video.removeAttribute('src');
    video.load();
    video.style.display = 'block';
  }
  if (modal) {
    modal.style.display = 'none';
  }
  currentPlayingItem = null;
}

function arePhotosIdentical(a, b) {
  if (!a || !b) return false;
  if (a.length !== b.length) return false;
  if (a.length === 0) return true;
  if (a[0].id !== b[0].id || a[a.length - 1].id !== b[b.length - 1].id) return false;
  for (let i = 0; i < a.length; i++) {
    if (a[i].id !== b[i].id ||
        a[i].isPending !== b[i].isPending ||
        a[i].isPendingIPhone !== b[i].isPendingIPhone ||
        a[i].isPendingAndroid !== b[i].isPendingAndroid ||
        a[i].isGooglePhotos !== b[i].isGooglePhotos) {
      return false;
    }
  }
  return true;
}

let lastSortKey = '0:d';

function albumSortOptions(sortMode, sortDescending) {
  const mode = Number(sortMode) || 0;
  const descending = sortDescending !== false;
  const flat = mode !== 0;
  return {
    key: mode + ':' + (descending ? 'd' : 'a'),
    order: flat ? 'keep' : (descending ? 'desc' : 'asc'),
    headerHeight: flat ? 0 : 40,
    groupSpacing: flat ? 8 : 18
  };
}

function initAlbum(photos, sortMode, sortDescending) {
  const boot = document.getElementById('gallery-boot');
  if (boot) boot.style.display = 'none';

  const sort = albumSortOptions(sortMode, sortDescending);
  const newPhotos = photos || [];
  if (albumInstance && arePhotosIdentical(newPhotos, currentPhotos) && lastSortKey === sort.key) {
    return;
  }
  lastSortKey = sort.key;

  const isDifferentQuery = currentPhotos.length === 0 || 
    (newPhotos.length > 0 && newPhotos[0].id !== currentPhotos[0]?.id);

  currentPhotos = newPhotos;
  updateEmptyState(currentPhotos.length);

  if (albumInstance) {
    if (isDifferentQuery && albumInstance.scroller) {
      albumInstance.scroller.scrollTop = 0;
    }
    albumInstance.setOptions({
      order: sort.order,
      headerHeight: sort.headerHeight,
      groupSpacing: sort.groupSpacing
    });
    albumInstance.setData(currentPhotos);
    return;
  }

  const container = document.getElementById('album-container');
  if (!container) return;

  albumInstance = new SweetAlbum(container, {
    data: currentPhotos,
    order: sort.order,
    locale: 'zh-CN',
    theme: 'light',
    gap: 6,
    targetRowHeight: 200,
    headerHeight: sort.headerHeight,
    groupSpacing: sort.groupSpacing,
    favorite: false,

    // Custom Badges on Corners
    badges: (item) => {
      const list = [];

      // Top Right: Upload / Sync Status Badges
      if (item.isPending || item.isPendingIPhone || item.isPendingAndroid) {
        list.push({
          id: 'badge-pending-unified',
          corner: 'topRight',
          className: 'badge-pill badge-pending',
          content: '<span class="badge-dot"></span>待传',
          title: '已加入待传列表（点击取消）',
          onClick: (it) => {
            postToHost({ action: 'toggleManualSelection', type: 'Unified', ids: [it.id] });
          }
        });
      }

      // Bottom Left: Clean Format & Media Type Badge
      const fmt = (item.format || 'IMG').toUpperCase();
      let typeIcon = '';
      let typeLabel = fmt;

      if (item.isLivePhoto) {
        typeIcon = '<svg class="badge-icon" viewBox="0 0 24 24"><circle cx="12" cy="12" r="9" fill="none" stroke="currentColor" stroke-width="2.2"/><circle cx="12" cy="12" r="5" fill="none" stroke="currentColor" stroke-width="2.2"/><circle cx="12" cy="12" r="1.5" fill="currentColor"/></svg>';
        typeLabel = '实况';
      } else if (item.isVideo) {
        typeIcon = '<svg class="badge-icon" viewBox="0 0 24 24"><polygon points="7 5 19 12 7 19 7 5" fill="currentColor"/></svg>';
        typeLabel = fmt;
      } else if (item.mediaType === 'screenshot') {
        typeIcon = '<svg class="badge-icon" viewBox="0 0 24 24"><rect x="5" y="2" width="14" height="20" rx="2" fill="none" stroke="currentColor" stroke-width="2"/><line x1="11" y1="18" x2="13" y2="18" stroke="currentColor" stroke-width="2" stroke-linecap="round"/></svg>';
        typeLabel = fmt;
      }

      list.push({
        id: 'badge-meta-format',
        corner: 'bottomLeft',
        className: `badge-pill badge-meta badge-fmt-${fmt.toLowerCase()}`,
        content: `<span class="badge-inner">${typeIcon}<span>${typeLabel}</span></span>`,
        title: `${fmt} 格式 · ${item.mediaType || '媒体'}`
      });

      return list;
    },

    // Custom Context Menu
    contextMenu: ({ item, selected, close }) => {
      if (!item) return false;
      const targets = selected && selected.some(p => p.id === item.id) && selected.length > 1 ? selected : [item];
      const ids = targets.map(t => t.id);
      const fullPath = item.fullPath;

      return [
        {
          id: 'mark-unified',
          label: `📌 加入待传列表 (${targets.length})`,
          onClick: () => postToHost({ action: 'addManualSelection', type: 'Unified', ids })
        },
        {
          id: 'unmark-unified',
          label: `✖ 从待传列表中移除 (${targets.length})`,
          onClick: () => postToHost({ action: 'removeManualSelection', ids })
        },
        { id: 'sep1', divider: true },
        {
          id: 'show-in-explorer',
          label: '📂 在资源管理器中显示',
          onClick: () => postToHost({ action: 'revealInExplorer', path: fullPath })
        },
        {
          id: 'open-external',
          label: '↗ 在默认应用中打开',
          onClick: () => postToHost({ action: 'openWith', path: fullPath })
        },
        {
          id: 'copy-file',
          label: '📋 复制文件',
          onClick: () => postToHost({ action: 'copyFile', path: fullPath })
        },
        {
          id: 'properties',
          label: 'ℹ 属性',
          onClick: () => postToHost({ action: 'showProperties', path: fullPath })
        }
      ];
    },

    // Batch Actions on Top Toolbar
    selectionActions: (selected) => [
      {
        id: 'batch-unified',
        label: `📌 加入待传列表 (${selected.length})`,
        icon: '<svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" stroke-width="2"><path d="M12 2v20M2 12h20"/></svg>',
        onClick: ({ selected, clearSelection }) => {
          postToHost({ action: 'addManualSelection', type: 'Unified', ids: selected.map(s => s.id) });
          clearSelection();
        }
      },
      {
        id: 'batch-remove',
        label: `✖ 移出待传列表 (${selected.length})`,
        danger: true,
        onClick: ({ selected, clearSelection }) => {
          postToHost({ action: 'removeManualSelection', ids: selected.map(s => s.id) });
          clearSelection();
        }
      }
    ],

    // Fullscreen preview is the native layer. The HTML viewer cannot sit on the same HWND.
    viewer: false,

    onItemClick: (rawItem, index, ev) => {
      if (!rawItem) return;
      if (ev) {
        ev.preventDefault();
      }
      requestNativePreview(rawItem, index);
    },

    onSelectionChange: (ids, items) => {
      postToHost({ action: 'selectionChanged', count: ids.length, ids });
    }
  });


  // Attach infinite scroll loader
  setTimeout(() => {
    const scroller = container.querySelector('.sp-scroller');
    if (scroller) {
      scroller.addEventListener('scroll', () => {
        if (scroller.scrollHeight - (scroller.scrollTop + scroller.clientHeight) < 400) {
          postToHost({ action: 'loadMore' });
        }
      }, { passive: true });
    }
  }, 100);
}

// Host message handler
function onHostMessage(event) {
  try {
    const data = typeof event.data === 'string' ? JSON.parse(event.data) : event.data;
    if (!data || !data.action) return;

    switch (data.action) {
      case 'setPhotos':
        initAlbum(data.photos || [], data.sortMode, data.sortDescending);
        break;

      case 'updateItems':
        if (albumInstance && Array.isArray(data.updates)) {
          const map = new Map(data.updates.map(u => [u.id, u]));
          currentPhotos = currentPhotos.map(item => {
            const upd = map.get(item.id);
            return upd ? { ...item, ...upd } : item;
          });
          albumInstance.setData(currentPhotos);
        }
        break;

      case 'setTheme':
        if (albumInstance && data.theme) {
          albumInstance.setTheme(data.theme);
        }
        break;
    }
  } catch (err) {
    console.error('Error processing host message:', err);
  }
}

if (window.chrome && window.chrome.webview) {
  window.chrome.webview.addEventListener('message', onHostMessage);
}

// Inform C# that web gallery is ready to receive data and bind modal buttons
window.addEventListener('DOMContentLoaded', () => {
  postToHost({ action: 'ready' });

  const closeBtn = document.getElementById('video-close-btn');
  const backdrop = document.getElementById('video-backdrop');
  const openSystemBtn = document.getElementById('video-btn-open-system');
  const revealBtn = document.getElementById('video-btn-reveal');

  if (closeBtn) closeBtn.addEventListener('click', closeVideoModal);
  if (backdrop) backdrop.addEventListener('click', closeVideoModal);

  function playingFilePath(item) {
    if (!item) return '';
    return item.liveVideoPath || item.fullPath || '';
  }

  if (openSystemBtn) {
    openSystemBtn.addEventListener('click', () => {
      const path = playingFilePath(currentPlayingItem);
      if (path) postToHost({ action: 'openWith', path });
    });
  }

  const errorOpenBtn = document.getElementById('video-error-open-btn');
  if (errorOpenBtn) {
    errorOpenBtn.addEventListener('click', () => {
      const path = playingFilePath(currentPlayingItem);
      if (path) postToHost({ action: 'openWith', path });
    });
  }

  if (revealBtn) {
    revealBtn.addEventListener('click', () => {
      const path = playingFilePath(currentPlayingItem);
      if (path) postToHost({ action: 'revealInExplorer', path });
    });
  }

  window.addEventListener('keydown', (e) => {
    if (e.key === 'Escape') {
      closeVideoModal();
    }
  });
});
