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

function openVideoModal(item) {
  if (!item) return;
  currentPlayingItem = item;
  const modal = document.getElementById('video-modal');
  const video = document.getElementById('html5-video-player');
  const title = document.getElementById('video-title');
  const meta = document.getElementById('video-meta-info');
  if (!modal || !video) return;

  title.textContent = item.relativePath || item.id || '视频播放';
  meta.textContent = `${(item.format || 'MP4').toUpperCase()} · ${item.takenAt || ''} · ${item.sizeText || ''}`;

  const videoUrl = item.url || `https://media.gallery.local/image?path=${encodeURIComponent(item.fullPath)}`;
  video.src = videoUrl;
  modal.style.display = 'flex';
  video.play().catch(e => console.log('Autoplay prevented or paused:', e));
}

function closeVideoModal() {
  const modal = document.getElementById('video-modal');
  const video = document.getElementById('html5-video-player');
  if (video) {
    video.pause();
    video.removeAttribute('src');
    video.load();
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
        a[i].isPendingIPhone !== b[i].isPendingIPhone ||
        a[i].isPendingAndroid !== b[i].isPendingAndroid ||
        a[i].isGooglePhotos !== b[i].isGooglePhotos) {
      return false;
    }
  }
  return true;
}

function initAlbum(photos) {
  const newPhotos = photos || [];
  if (albumInstance && arePhotosIdentical(newPhotos, currentPhotos)) {
    return;
  }

  const isDifferentQuery = currentPhotos.length === 0 || 
    (newPhotos.length > 0 && newPhotos[0].id !== currentPhotos[0]?.id);

  currentPhotos = newPhotos;
  updateEmptyState(currentPhotos.length);

  if (albumInstance) {
    if (isDifferentQuery && albumInstance.scroller) {
      albumInstance.scroller.scrollTop = 0;
    }
    albumInstance.setData(currentPhotos);
    return;
  }

  const container = document.getElementById('album-container');
  if (!container) return;

  albumInstance = new SweetAlbum(container, {
    data: currentPhotos,
    order: 'desc',
    locale: 'zh-CN',
    theme: 'light',
    gap: 6,
    targetRowHeight: 200,
    headerHeight: 40,
    groupSpacing: 18,
    favorite: false,

    // Custom Badges on Corners
    badges: (item) => {
      const list = [];

      // Top Right: Upload / Sync Status Badges
      if (item.isPendingIPhone) {
        list.push({
          id: 'badge-pending-iphone',
          corner: 'topRight',
          className: 'badge-pill badge-iphone',
          content: '<span class="badge-dot"></span>iPhone 待传',
          title: '已标记为 iPhone 待传清单（点击取消）',
          onClick: (it) => {
            postToHost({ action: 'toggleManualSelection', type: 'iPhone', ids: [it.id] });
          }
        });
      } else if (item.isPendingAndroid) {
        list.push({
          id: 'badge-pending-android',
          corner: 'topRight',
          className: 'badge-pill badge-android',
          content: '<span class="badge-dot"></span>Android 待传',
          title: '已标记为 Android 待传清单（点击取消）',
          onClick: (it) => {
            postToHost({ action: 'toggleManualSelection', type: 'Android', ids: [it.id] });
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
          id: 'mark-iphone',
          label: `📌 加入 iPhone 待传清单 (${targets.length})`,
          onClick: () => postToHost({ action: 'addManualSelection', type: 'iPhone', ids })
        },
        {
          id: 'mark-android',
          label: `📌 加入 Android 待传清单 (${targets.length})`,
          onClick: () => postToHost({ action: 'addManualSelection', type: 'Android', ids })
        },
        {
          id: 'mark-google',
          label: `☁ 加入 Google Photos 待传 (${targets.length})`,
          onClick: () => postToHost({ action: 'addManualSelection', type: 'Google', ids })
        },
        {
          id: 'unmark-all',
          label: `✖ 从待传清单中移除 (${targets.length})`,
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
        id: 'batch-iphone',
        label: `加入 iPhone 待传 (${selected.length})`,
        icon: '<svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" stroke-width="2"><rect x="5" y="2" width="14" height="20" rx="3"/><circle cx="12" cy="18" r="1"/></svg>',
        onClick: ({ selected, clearSelection }) => {
          postToHost({ action: 'addManualSelection', type: 'iPhone', ids: selected.map(s => s.id) });
          clearSelection();
        }
      },
      {
        id: 'batch-android',
        label: `加入 Android 待传 (${selected.length})`,
        icon: '<svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" stroke-width="2"><path d="M4 10h16v10a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2V10z"/><path d="M8 6l-2-3M16 6l2-3"/><circle cx="9" cy="8" r="1"/><circle cx="15" cy="8" r="1"/></svg>',
        onClick: ({ selected, clearSelection }) => {
          postToHost({ action: 'addManualSelection', type: 'Android', ids: selected.map(s => s.id) });
          clearSelection();
        }
      },
      {
        id: 'batch-remove',
        label: `移出待传清单 (${selected.length})`,
        danger: true,
        onClick: ({ selected, clearSelection }) => {
          postToHost({ action: 'removeManualSelection', ids: selected.map(s => s.id) });
          clearSelection();
        }
      }
    ],

    // Fullscreen Viewer Configuration
    viewer: {
      initialFit: 'contain',
      maxScale: 8,
      zoomStep: 1.25,
      arrows: true,
      actions: [
        'rotateLeft',
        'rotateRight',
        'divider',
        'zoomOut',
        'zoomLevel',
        'zoomIn',
        'divider',
        'actualSize',
        'fit',
        'divider',
        {
          id: 'viewer-play-video',
          title: '播放实况视频 / 视频',
          icon: '<svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" stroke-width="2"><circle cx="12" cy="12" r="9"/><polygon points="10 8 16 12 10 16 10 8" fill="currentColor"/></svg>',
          onClick: ({ state }) => {
            if (state && state.item) {
              openVideoModal(state.item);
            }
          }
        },
        {
          id: 'viewer-external',
          title: '在系统默认应用中打开',
          icon: '<svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" stroke-width="2"><path d="M18 13v6a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2h6"/><polyline points="15 3 21 3 21 9"/><line x1="10" y1="14" x2="21" y2="3"/></svg>',
          onClick: ({ state }) => {
            if (state && state.item && state.item.fullPath) {
              postToHost({ action: 'openWith', path: state.item.fullPath });
            }
          }
        },
        {
          id: 'viewer-explorer',
          title: '在资源管理器中显示',
          icon: '<svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" stroke-width="2"><path d="M22 19a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h5l2 3h9a2 2 0 0 1 2 2z"/></svg>',
          onClick: ({ state }) => {
            if (state && state.item && state.item.fullPath) {
              postToHost({ action: 'revealInExplorer', path: state.item.fullPath });
            }
          }
        }
      ]
    },

    onItemClick: (rawItem, index, ev) => {
      if (rawItem && rawItem.isVideo) {
        if (ev) {
          ev.preventDefault();
        }
        openVideoModal(rawItem);
        return;
      }
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
        initAlbum(data.photos || []);
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

  if (openSystemBtn) {
    openSystemBtn.addEventListener('click', () => {
      if (currentPlayingItem && currentPlayingItem.fullPath) {
        postToHost({ action: 'openWith', path: currentPlayingItem.fullPath });
      }
    });
  }

  if (revealBtn) {
    revealBtn.addEventListener('click', () => {
      if (currentPlayingItem && currentPlayingItem.fullPath) {
        postToHost({ action: 'revealInExplorer', path: currentPlayingItem.fullPath });
      }
    });
  }

  window.addEventListener('keydown', (e) => {
    if (e.key === 'Escape') {
      closeVideoModal();
    }
  });
});
