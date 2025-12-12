let connection = null;
let targetIp = null;
let isWebcamStreaming = false;
let frameCount = 0;
let lastFpsUpdate = Date.now();
let serverInfo = { ip: null, port: null };
let isMenuOpen = false;

document.addEventListener('DOMContentLoaded', async () => {
    await loadServerInfo();
    await setupSignalR();
});

function selectMode(mode) {
    const contentOverlay = document.getElementById('contentOverlay');
    const heroSection = document.querySelector('.hero-section');
    const body = document.body;
    
    if (mode === null) {
        // Quay lại trang chủ
        contentOverlay.classList.remove('active');
        body.classList.remove('overlay-active');
        heroSection.style.display = 'flex';
        document.getElementById('loginSection').classList.remove('active');
        document.getElementById('controlledSection').classList.remove('active');
        document.getElementById('controlPanel').classList.remove('active');
    } else {
        // Hiển thị overlay và ẩn hero
        heroSection.style.display = 'none';
        contentOverlay.classList.add('active');
        body.classList.add('overlay-active');
        document.getElementById('loginSection').classList.remove('active');
        document.getElementById('controlledSection').classList.remove('active');
        document.getElementById('controlPanel').classList.remove('active');

        if (mode === 'controller') {
            document.getElementById('loginSection').classList.add('active');
        } else if (mode === 'controlled') {
            document.getElementById('controlledSection').classList.add('active');
            refreshSessions();
        }
    }
}

function switchTab(tabName) {
    document.querySelectorAll('.tab').forEach(t => t.classList.remove('active'));
    document.querySelectorAll('.tab-content').forEach(c => c.classList.remove('active'));
    
    // Tìm tab button tương ứng và kích hoạt
    const tabButtons = document.querySelectorAll('.tab');
    tabButtons.forEach(btn => {
        if (btn.textContent.includes(getTabEmoji(tabName))) {
            btn.classList.add('active');
        }
    });
    
    document.getElementById(tabName + 'Tab').classList.add('active');
}

function getTabEmoji(tabName) {
    const emojiMap = {
        'apps': '📱',
        'processes': '⚙️',
        'screen': '📸',
        'keylog': '⌨️',
        'webcam': '📹',
        'system': '🔋'
    };
    return emojiMap[tabName] || '';
}

async function setupSignalR() {
    if (!connection) {
        connection = new signalR.HubConnectionBuilder()
            .withUrl("/controlHub")
            .build();

        connection.on("ReceiveResponse", (response) => {
            console.log("📥", response.substring(0, 100));
            handleResponse(response);
        });
    }

    if (connection.state === "Connected" || connection.state === "Connecting") return;

    try {
        await connection.start();
        console.log("✅ SignalR connected!");
    } catch (err) {
        console.error("❌ SignalR failed:", err);
        alert("Không thể kết nối đến server");
    }
}

function handleResponse(response) {
    const parts = response.split('|');
    if (parts.length < 2) return;

    const responseType = parts[1];
    const data = parts.slice(2).join('|');

    switch (responseType) {
        case 'SUCCESS':
            if (!data.includes('Webcam') && !data.includes('streaming')) {
                 document.getElementById('loginError').textContent = "";
            }
            document.getElementById('loginSection').classList.remove('active');
            document.getElementById('controlledSection').classList.remove('active');
            document.getElementById('controlPanel').classList.add('active');
            // Kiểm tra task đang chờ
            checkPendingTask();
            break;
        case 'FAILED':
            document.getElementById('loginError').textContent = "Sai tài khoản hoặc mật khẩu!";
            document.getElementById('loginError').style.display = "block";
            break;
        case 'ERROR':
            alert('⚠️ Lỗi: ' + data);
            break;
        case 'SCREENSHOT':
            document.getElementById('screenshotContainer').innerHTML =
                `<img src="data:image/jpeg;base64,${data}" class="screenshot-preview">`;
            break;
        case 'WEBCAM_IMAGE':
            addWebcamSnapshot(data);
            break;
        case 'WEBCAM_FRAME':
            if (isWebcamStreaming) updateWebcamFrame(data);
            break;
        case 'APPS':
            displayApps(data);
            break;
        case 'PROCESSES':
            displayProcesses(data);
            break;
        case 'KEYLOGS':
            document.getElementById('keylogDisplay').textContent = data || "Không có keylogs.";
            break;
        case 'SESSIONS':
            renderSessions(data);
            break;
        case 'SEARCH_APPS':
            displayAppSearchResults(data);
            break;
    }
}

async function loadServerInfo() {
    try {
        const res = await fetch('/server-info.json');
        if (!res.ok) throw new Error(res.statusText);
        const json = await res.json();
        serverInfo = { ip: json.Ip, port: json.Port };
        document.getElementById('serverEndpoint').textContent = `${json.Ip}:${json.Port}`;
    } catch {
        document.getElementById('serverEndpoint').textContent = 'Không lấy được IP server, dùng thủ công.';
    }
}

function downloadClientPackage() {
    window.location.href = '/downloads/client-controlled.zip';
}

async function refreshSessions() {
    await setupSignalR();
    if (connection && connection.state === "Connected") {
        connection.invoke("RequestSessions").catch(err => console.error(err));
    }
}

function renderSessions(data) {
    const container = document.getElementById('sessionList');
    if (!data) {
        container.textContent = "Chưa có máy kết nối.";
        return;
    }

    const items = data.split('||').filter(Boolean);
    if (items.length === 0) {
        container.textContent = "Chưa có máy kết nối.";
        return;
    }

    container.innerHTML = '';
    items.forEach(item => {
        const [ip, password, status] = item.split(':');
        const card = document.createElement('div');
        card.className = 'session-item';
        card.innerHTML = `
            <div class="session-meta">
                <strong>${ip || 'N/A'}</strong>
                <span>Password: ${password || 'N/A'}</span>
            </div>
            <span class="session-status ${status === 'WAITING' ? 'waiting' : ''}">${status || 'UNKNOWN'}</span>
        `;
        container.appendChild(card);
    });
}

function updateWebcamFrame(base64Data) {
    let container = document.getElementById('webcamContainer');
    let img = document.getElementById('webcamStreamImg');

    if (!img) {
        container.innerHTML = `
                <img id="webcamStreamImg" class="webcam-stream">
                <div class="webcam-overlay">
                    <div class="webcam-status">
                        <div class="live-indicator">
                            <div class="live-dot"></div>
                            LIVE
                        </div>
                    </div>
                    <div class="webcam-fps" id="webcamFps">-- FPS</div>
                </div>
            `;
        img = document.getElementById('webcamStreamImg');
    }

    img.src = 'data:image/jpeg;base64,' + base64Data;

    frameCount++;
    const now = Date.now();
    if (now - lastFpsUpdate >= 1000) {
        const fps = Math.round(frameCount / ((now - lastFpsUpdate) / 1000));
        document.getElementById('webcamFps').textContent = `${fps} FPS`;
        frameCount = 0;
        lastFpsUpdate = now;
    }
}

function addWebcamSnapshot(base64Data) {
    const container = document.getElementById('webcamSnapshots');
    const time = new Date().toLocaleTimeString();
    const snapshot = document.createElement('div');
    snapshot.className = 'snapshot-item';
    snapshot.innerHTML = `
            <img src="data:image/jpeg;base64,${base64Data}">
            <div class="snapshot-time">${time}</div>
        `;
    container.insertBefore(snapshot, container.firstChild);
}

function displayApps(data) {
    let html = '';
    const apps = data.split('||');
    apps.forEach(appStr => {
        if (appStr) {
            const [name, id, title] = appStr.split(':');
            html += `
                    <div class="list-item">
                        <div class="list-item-info">
                            <h4>${name || 'N/A'}</h4>
                            <p>${title || '...'} (PID: ${id || 'N/A'})</p>
                        </div>
                        <button class="btn-stop" onclick="stopApp('${id}')">Stop</button>
                    </div>`;
        }
    });
    document.getElementById('appsList').innerHTML = html || "<p>Không có ứng dụng.</p>";
}

function displayAppSearchResults(data) {
    const container = document.getElementById('appSearchResults');
    
    if (!data || data.trim() === '') {
        container.innerHTML = `
            <div class="search-placeholder">
                <div class="icon">❌</div>
                <p>Không tìm thấy ứng dụng nào</p>
            </div>
        `;
        return;
    }

    const apps = data.split('||').filter(Boolean);
    if (apps.length === 0) {
        container.innerHTML = `
            <div class="search-placeholder">
                <div class="icon">❌</div>
                <p>Không tìm thấy ứng dụng nào</p>
            </div>
        `;
        return;
    }

    let html = '<div class="app-results-list">';
    apps.forEach((appStr, index) => {
        if (appStr) {
            const parts = appStr.split(':');
            const appName = parts[0] || 'N/A';
            const appPath = parts.slice(1).join(':'); // Join lại vì path có thể chứa ':'
            
            // Lấy tên file để hiển thị
            const fileName = appPath.split('\\').pop() || appPath.split('/').pop() || appPath;
            
            // Sử dụng data attributes để tránh vấn đề escape
            html += `
                <div class="app-result-item" data-app-path="${escapeHtml(appPath)}" data-app-name="${escapeHtml(appName)}">
                    <div class="app-result-icon">📱</div>
                    <div class="app-result-info">
                        <h4>${escapeHtml(appName)}</h4>
                        <p>${escapeHtml(fileName)}</p>
                    </div>
                    <div class="app-result-action">
                        <i class="fas fa-play"></i>
                    </div>
                </div>
            `;
        }
    });
    html += '</div>';
    
    container.innerHTML = html;
    
    // Thêm event listeners sau khi render
    container.querySelectorAll('.app-result-item').forEach(item => {
        item.addEventListener('click', function() {
            const appPath = this.getAttribute('data-app-path');
            const appName = this.getAttribute('data-app-name');
            startAppFromSearch(appPath, appName);
        });
    });
}

function escapeHtml(text) {
    const div = document.createElement('div');
    div.textContent = text;
    return div.innerHTML;
}

function displayProcesses(data) {
    let html = '';
    const processes = data.split('||');
    processes.forEach(procStr => {
        if (procStr) {
            const [name, id, memory] = procStr.split(':');
            html += `
                    <div class="list-item">
                        <div class="list-item-info">
                            <h4>${name || 'N/A'}</h4>
                            <p>PID: ${id || 'N/A'} | ${memory || 'N/A'}</p>
                        </div>
                        <button class="btn-stop" onclick="stopProcess('${id}')">Kill</button>
                    </div>`;
        }
    });
    document.getElementById('processesList').innerHTML = html || "<p>Không có process.</p>";
}

function sendCommand(commandType, parameters = '') {
    if (!connection || !targetIp) {
        alert("Chưa kết nối!");
        return;
    }
    connection.invoke("SendCommandToServer", commandType, targetIp, parameters)
        .catch(err => console.error(err));
}

async function login() {
    const ip = document.getElementById('loginIp').value;
    const password = document.getElementById('loginPassword').value;

    if (!ip || !password) {
        document.getElementById('loginError').textContent = "Vui lòng nhập đầy đủ thông tin!";
        document.getElementById('loginError').style.display = "block";
        return;
    }


    await setupSignalR();

    if (connection && connection.state === "Connected") {
        targetIp = ip;
        document.getElementById('connectedIp').textContent = 'Đang điều khiển: ' + ip;
        sendCommand("LOGIN", password);
    }
   
}

async function disconnect() {
    // Dừng webcam streaming nếu đang chạy
    if (isWebcamStreaming) {
        await stopWebcamStream();
        await new Promise(resolve => setTimeout(resolve, 200));
        sendCommand('WEBCAM_OFF');
    }
    
    if (connection) {
        connection.stop();
        connection = null;
    }
    targetIp = null;
    document.getElementById('controlPanel').classList.remove('active');
    // Xóa pending task khi ngắt kết nối
    sessionStorage.removeItem('pendingTask');
    selectMode(null);
}

function listApplications() { sendCommand('LIST_APPS'); }

function openAppSearchModal() {
    const modal = document.getElementById('appSearchModal');
    modal.classList.add('active');
    document.getElementById('appSearchInput').focus();
    
    // Đóng modal khi click bên ngoài
    modal.addEventListener('click', function(e) {
        if (e.target === modal) {
            closeAppSearchModal();
        }
    });
}

function closeAppSearchModal() {
    document.getElementById('appSearchModal').classList.remove('active');
    document.getElementById('appSearchInput').value = '';
    document.getElementById('appSearchResults').innerHTML = `
        <div class="search-placeholder">
            <div class="icon">🔍</div>
            <p>Nhập tên ứng dụng và nhấn Enter hoặc nút "Tìm kiếm"</p>
        </div>
    `;
}

function handleAppSearch(event) {
    if (event.key === 'Enter') {
        performAppSearch();
    }
}

function performAppSearch() {
    const searchQuery = document.getElementById('appSearchInput').value.trim();
    if (!searchQuery) {
        alert('Vui lòng nhập tên ứng dụng!');
        return;
    }

    // Hiển thị loading
    document.getElementById('appSearchResults').innerHTML = `
        <div class="search-loading">
            <div class="spinner"></div>
            <p>Đang tìm kiếm...</p>
        </div>
    `;

    // Gửi lệnh tìm kiếm
    sendCommand('SEARCH_APPS', searchQuery);
}

function startApp() {
    const path = prompt('Đường dẫn (VD: notepad.exe):');
    if (path) sendCommand('START_APP', path);
}

function startAppFromSearch(appPath, appName) {
    sendCommand('START_APP', appPath);
    closeAppSearchModal();
    // Hiển thị thông báo
    setTimeout(() => {
        alert(`Đã khởi chạy: ${appName}`);
    }, 500);
}
function stopApp(id) {
    if (confirm('Dừng app?')) sendCommand('STOP_APP', id);
}

function listProcesses() { sendCommand('LIST_PROCESSES'); }
function startProcess() {
    const name = prompt('Tên process:');
    if (name) sendCommand('START_PROCESS', name);
}
function stopProcess(id) {
    if (confirm('Kill process?')) sendCommand('STOP_PROCESS', id);
}

function takeScreenshot() {
    sendCommand('SCREENSHOT');
    document.getElementById('screenshotContainer').innerHTML = "<p>⏳ Đang chụp...</p>";
}

function getKeylogs() { sendCommand('GET_KEYLOGS'); }
function clearKeylogs() {
    if (confirm('Xóa keylogs?')) sendCommand('CLEAR_KEYLOGS');
}

function startWebcam() {
    sendCommand('WEBCAM_ON');// web cam on trc
    document.getElementById('webcamContainer').innerHTML =
        '<div class="webcam-placeholder"><div class="icon">📹</div><h3>Đang khởi động...</h3></div>';

     if (isWebcamStreaming) {
        alert('Stream đang chạy!');
        return;
    }
    sendCommand('WEBCAM_STREAM_START');
    isWebcamStreaming = true;
    frameCount = 0;
    lastFpsUpdate = Date.now();
}   
function captureWebcam() {
    sendCommand('WEBCAM_CAPTURE');
}
function stopWebcamStream() {
    if (isWebcamStreaming) {
        isWebcamStreaming = false;
        if (connection && connection.state === "Connected") {
            return connection.invoke("SendCommandToServer", "WEBCAM_STREAM_STOP", targetIp, "")
                .catch(err => console.error(err));
        }
    }
    return Promise.resolve();
}

async function stopWebcam() {
    // Dừng streaming trước
    await stopWebcamStream();
    
    // Đợi một chút để đảm bảo streaming đã dừng hoàn toàn
    await new Promise(resolve => setTimeout(resolve, 300));
    
    // Sau đó mới tắt webcam
    sendCommand('WEBCAM_OFF');
    
    // Cập nhật UI
    document.getElementById('webcamContainer').innerHTML =
        '<div class="webcam-placeholder"><div class="icon">📹</div><h3>Webcam đã tắt</h3></div>';
    document.getElementById('webcamSnapshots').innerHTML = '';   
}

function shutdownPC() {
    if (confirm('⚠️ TẮT MÁY?')) sendCommand('SHUTDOWN');
}
function restartPC() {
    if (confirm('⚠️ RESTART?')) sendCommand('RESTART');
}

function toggleMenu() {
    const menu = document.getElementById('menuDropdown');
    isMenuOpen = !isMenuOpen;
    if (isMenuOpen) {
        menu.classList.add('active');
    } else {
        menu.classList.remove('active');
    }
}

function navigateToTask(taskName) {
    // Đóng menu
    isMenuOpen = false;
    document.getElementById('menuDropdown').classList.remove('active');
    
    // Kiểm tra đăng nhập - kiểm tra cả targetIp và connection
    if (!targetIp || !connection || connection.state !== "Connected") {
        // Chưa đăng nhập, hiển thị form đăng nhập
        selectMode('controller');
        // Lưu task cần chuyển đến sau khi đăng nhập
        sessionStorage.setItem('pendingTask', taskName);
        // Hiển thị thông báo
        setTimeout(() => {
            alert('Vui lòng đăng nhập để sử dụng tính năng này!');
        }, 100);
        return;
    }
    
    // Đã đăng nhập, chuyển đến task
    // Đảm bảo control panel được hiển thị
    const contentOverlay = document.getElementById('contentOverlay');
    const heroSection = document.querySelector('.hero-section');
    const body = document.body;
    
    heroSection.style.display = 'none';
    contentOverlay.classList.add('active');
    body.classList.add('overlay-active');
    document.getElementById('loginSection').classList.remove('active');
    document.getElementById('controlledSection').classList.remove('active');
    document.getElementById('controlPanel').classList.add('active');
    
    // Chuyển đến tab tương ứng
    switchTab(taskName);
}

// Kiểm tra task đang chờ sau khi đăng nhập thành công
function checkPendingTask() {
    const pendingTask = sessionStorage.getItem('pendingTask');
    if (pendingTask) {
        sessionStorage.removeItem('pendingTask');
        // Đợi một chút để control panel hiển thị
        setTimeout(() => {
            switchTab(pendingTask);
        }, 300);
    }
}