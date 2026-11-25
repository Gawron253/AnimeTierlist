// In-app YouTube player bootstrap. Loads YouTube's official IFrame Player API and reports status to the app.
// Messages are plain strings: "ready", "started", "unreachable", or "error:<IFrame API code>". No URL is ever sent
// to the app.
(function () {
  'use strict';

  function post(message) {
    if (window.chrome && window.chrome.webview) {
      window.chrome.webview.postMessage(message);
    }
  }

  var id = new URLSearchParams(window.location.search).get('v');
  if (!id || !/^[A-Za-z0-9_-]{11}$/.test(id)) {
    post('error:2');
    return;
  }

  var started = false;

  window.onYouTubeIframeAPIReady = function () {
    // eslint-disable-next-line no-new
    new window.YT.Player('player', {
      host: 'https://www.youtube-nocookie.com',
      videoId: id,
      width: '100%',
      height: '100%',
      playerVars: {
        autoplay: 1,
        playsinline: 1,
        rel: 0,
        iv_load_policy: 3,
        origin: window.location.origin
      },
      events: {
        onReady: function () { post('ready'); },
        onStateChange: function (event) {
          if (!started && event.data === window.YT.PlayerState.PLAYING) {
            started = true;
            post('started');
          }
        },
        onError: function (event) { post('error:' + String(event.data)); }
      }
    });
  };

  var script = document.createElement('script');
  script.src = 'https://www.youtube.com/iframe_api';
  script.onerror = function () { post('unreachable'); };
  document.head.appendChild(script);
}());
