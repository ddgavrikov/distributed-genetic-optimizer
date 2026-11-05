(() => {
    const byId = id => document.getElementById(id);
    const form = byId('optimizer-form');

    if (!form) {
        return;
    }

    let activeJobId = null;
    let pollTimer = null;

    const addresses = () => byId('serviceAddresses')
        .value
        .split(/[\n,;]+/)
        .map(value => value.trim())
        .filter(Boolean);

    const number = id => Number(byId(id).value);

    byId('generate-button').addEventListener('click', generateCities);
    byId('check-button').addEventListener('click', checkWorkers);
    byId('stop-button').addEventListener('click', cancelJob);

    form.addEventListener('submit', async event => {
        event.preventDefault();
        clearTimeout(pollTimer);

        try {
            const payload = {
                serviceAddresses: addresses(),
                cities: parseCities(),
                populationSize: number('populationSize'),
                generations: number('generations'),
                mutationProbability: number('mutationPercent') / 100,
                eliteCount: number('eliteCount'),
                threads: number('threads'),
                seed: number('seed')
            };

            setRunning(true);
            showMessage('Задача отправляется вычислительным сервисам…', 'info');

            const response = await fetch('/api/optimization/start', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(payload)
            });
            const data = await response.json();

            if (!response.ok) {
                throw new Error(data.error || 'Не удалось запустить задачу.');
            }

            activeJobId = data.jobId;
            await pollJob();
        } catch (error) {
            setRunning(false);
            showMessage(error.message, 'error');
        }
    });

    async function generateCities() {
        const button = byId('generate-button');
        button.disabled = true;
        button.textContent = 'Генерация…';

        try {
            const response = await fetch('/api/optimization/generate', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({
                    count: number('cityCount'),
                    seed: number('citySeed')
                })
            });
            const cities = await response.json();

            if (!response.ok) {
                throw new Error(cities.error || 'Не удалось создать города.');
            }

            byId('coordinates').value = cities
                .map(city => `${city.x} ${city.y}`)
                .join('\n');

            drawRoute(cities, null);
            showMessage(`Создано городов: ${cities.length}.`, 'success');
        } catch (error) {
            showMessage(error.message, 'error');
        } finally {
            button.disabled = false;
            button.textContent = 'Создать города';
        }
    }

    async function checkWorkers() {
        const button = byId('check-button');
        button.disabled = true;
        button.textContent = 'Запуск и проверка…';
        byId('health-results').innerHTML = '';

        try {
            const response = await fetch('/api/optimization/health', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ serviceAddresses: addresses() })
            });
            const data = await response.json();

            if (!response.ok) {
                throw new Error(data.error || 'Не удалось проверить сервисы.');
            }

            byId('health-results').innerHTML = data
                .map(createHealthResult)
                .join('');
        } catch (error) {
            byId('health-results').innerHTML =
                `<div class="status-item fail">${escapeHtml(error.message)}</div>`;
        } finally {
            button.disabled = false;
            button.textContent = 'Запустить и проверить';
        }
    }

    function createHealthResult(item) {
        const status = item.available ? 'ok' : 'fail';
        const address = escapeHtml(item.address);
        const message = escapeHtml(item.message);

        return `<div class="status-item ${status}">
            <span class="dot"></span>
            <code>${address}</code>
            <span>${message} · ${item.elapsedMilliseconds} мс</span>
        </div>`;
    }

    async function pollJob() {
        if (!activeJobId) {
            return;
        }

        try {
            const response = await fetch(`/api/optimization/jobs/${activeJobId}`);
            const snapshot = await response.json();

            if (!response.ok) {
                throw new Error(snapshot.error || 'Не удалось получить состояние задачи.');
            }

            renderSnapshot(snapshot);

            if (snapshot.status === 'queued' || snapshot.status === 'running') {
                pollTimer = setTimeout(pollJob, 250);
                return;
            }

            setRunning(false);
            activeJobId = null;
            showMessage(
                snapshot.message,
                snapshot.status === 'failed' ? 'error' : 'success'
            );
        } catch (error) {
            setRunning(false);
            activeJobId = null;
            showMessage(error.message, 'error');
        }
    }

    async function cancelJob() {
        if (!activeJobId) {
            return;
        }

        byId('stop-button').disabled = true;
        showMessage('Останавливаем вычисления на всех узлах…', 'info');

        try {
            await fetch(`/api/optimization/jobs/${activeJobId}/cancel`, {
                method: 'POST'
            });
        } catch {
            showMessage('Не удалось отправить команду остановки.', 'error');
        }
    }

    function renderSnapshot(snapshot) {
        const percent = snapshot.targetGenerations > 0
            ? Math.min(100, snapshot.generation / snapshot.targetGenerations * 100)
            : 0;

        byId('progress-bar').style.width = `${percent}%`;
        showMessage(
            `${snapshot.message} Поколение ${snapshot.generation} из ${snapshot.targetGenerations}.`,
            'info'
        );

        byId('workers-body').innerHTML = snapshot.workers
            .map(createWorkerRow)
            .join('');

        if (!snapshot.bestRoute || snapshot.bestLength == null) {
            return;
        }

        byId('results').classList.remove('hidden');
        byId('best-length').textContent = format(snapshot.bestLength);
        byId('generation-value').textContent =
            `${snapshot.generation} / ${snapshot.targetGenerations}`;
        byId('elapsed').textContent =
            `${(snapshot.elapsedMilliseconds / 1000).toFixed(1)} с`;

        renderRouteDetails(snapshot.cities, snapshot.bestRoute);
        drawRoute(snapshot.cities, snapshot.bestRoute);
        drawHistory(snapshot.history);
    }

    function createWorkerRow(worker) {
        const error = worker.error
            ? `<div class="error-detail">${escapeHtml(worker.error)}</div>`
            : '';
        const bestLength = worker.bestLength == null
            ? '—'
            : format(worker.bestLength);

        return `<tr>
            <td><code>${escapeHtml(worker.address)}</code></td>
            <td>
                <span class="pill ${worker.status}">${escapeHtml(statusText(worker.status))}</span>
                ${error}
            </td>
            <td>${worker.generation}</td>
            <td>${bestLength}</td>
        </tr>`;
    }

    function renderRouteDetails(cities, route) {
        const closedRoute = [...route, route[0]];
        byId('route-sequence').textContent = closedRoute
            .map(index => index + 1)
            .join(' → ');

        byId('edges-body').innerHTML = route
            .map((cityIndex, position) => {
                const nextIndex = route[(position + 1) % route.length];
                const edgeLength = distance(cities[cityIndex], cities[nextIndex]);

                return `<tr>
                    <td>Город ${cityIndex + 1} → город ${nextIndex + 1}</td>
                    <td>${format(edgeLength)}</td>
                </tr>`;
            })
            .join('');
    }

    function parseCities() {
        const lines = byId('coordinates')
            .value
            .split(/\r?\n/)
            .map(line => line.trim())
            .filter(Boolean);

        if (lines.length < 3 || lines.length > 500) {
            throw new Error('Введите от 3 до 500 городов.');
        }

        return lines.map((line, index) => {
            const values = line.split(/\s+/).map(Number);

            if (values.length !== 2 || values.some(value => !Number.isFinite(value))) {
                throw new Error(`Строка ${index + 1}: нужны два числа X Y.`);
            }

            return { x: values[0], y: values[1] };
        });
    }

    function setRunning(running) {
        byId('run-button').disabled = running;
        byId('stop-button').disabled = !running;
        byId('run-button').querySelector('span').textContent = running
            ? 'Поиск выполняется…'
            : 'Начать поиск';
    }

    function showMessage(text, type) {
        const message = byId('main-message');
        message.textContent = text;
        message.className = `main-message ${type}`;
    }

    function drawRoute(cities, route) {
        const { ctx, width, height } = prepareCanvas('route-canvas', 520);
        ctx.clearRect(0, 0, width, height);

        if (!cities || cities.length === 0) {
            return;
        }

        const padding = 38;
        const xCoordinates = cities.map(city => city.x);
        const yCoordinates = cities.map(city => city.y);
        const minX = Math.min(...xCoordinates);
        const maxX = Math.max(...xCoordinates);
        const minY = Math.min(...yCoordinates);
        const maxY = Math.max(...yCoordinates);
        const scale = Math.min(
            (width - padding * 2) / Math.max(1, maxX - minX),
            (height - padding * 2) / Math.max(1, maxY - minY)
        );

        const point = index => ({
            x: padding + (cities[index].x - minX) * scale,
            y: height - padding - (cities[index].y - minY) * scale
        });

        if (route && route.length > 0) {
            ctx.strokeStyle = '#17745a';
            ctx.lineWidth = 2.3;
            ctx.beginPath();

            route.forEach((city, index) => {
                const routePoint = point(city);

                if (index === 0) {
                    ctx.moveTo(routePoint.x, routePoint.y);
                } else {
                    ctx.lineTo(routePoint.x, routePoint.y);
                }
            });

            const start = point(route[0]);
            ctx.lineTo(start.x, start.y);
            ctx.stroke();
        }

        cities.forEach((_, index) => {
            const cityPoint = point(index);
            ctx.beginPath();
            ctx.fillStyle = index === 0 ? '#dc5f42' : '#122820';
            ctx.arc(cityPoint.x, cityPoint.y, index === 0 ? 6 : 4, 0, Math.PI * 2);
            ctx.fill();

            if (cities.length <= 100) {
                ctx.fillStyle = '#243a31';
                ctx.font = '12px system-ui';
                ctx.fillText(String(index + 1), cityPoint.x + 7, cityPoint.y - 7);
            }
        });
    }

    function drawHistory(history) {
        const { ctx, width, height } = prepareCanvas('history-chart', 300);
        ctx.clearRect(0, 0, width, height);

        if (!history || history.length === 0) {
            return;
        }

        const padding = { left: 62, right: 22, top: 22, bottom: 38 };
        const values = history.map(point => point.length);
        let min = Math.min(...values);
        let max = Math.max(...values);

        if (max - min < 1e-9) {
            min -= 1;
            max += 1;
        }

        ctx.strokeStyle = '#d7ded9';
        ctx.fillStyle = '#66736c';
        ctx.font = '11px system-ui';
        ctx.lineWidth = 1;

        for (let index = 0; index <= 4; index++) {
            const y = padding.top
                + (height - padding.top - padding.bottom) * index / 4;

            ctx.beginPath();
            ctx.moveTo(padding.left, y);
            ctx.lineTo(width - padding.right, y);
            ctx.stroke();
            ctx.fillText((max - (max - min) * index / 4).toFixed(1), 5, y + 4);
        }

        ctx.strokeStyle = '#17745a';
        ctx.lineWidth = 3;
        ctx.beginPath();

        values.forEach((value, index) => {
            const x = padding.left
                + (width - padding.left - padding.right)
                * index / Math.max(1, values.length - 1);
            const y = padding.top
                + (height - padding.top - padding.bottom)
                * (max - value) / (max - min);

            if (index === 0) {
                ctx.moveTo(x, y);
            } else {
                ctx.lineTo(x, y);
            }
        });

        ctx.stroke();
        ctx.fillStyle = '#66736c';
        ctx.fillText(String(history[0].generation), padding.left, height - 12);
        ctx.fillText(
            `поколение ${history[history.length - 1].generation}`,
            Math.max(padding.left, width - 120),
            height - 12
        );
    }

    function prepareCanvas(id, height) {
        const canvas = byId(id);
        const ratio = window.devicePixelRatio || 1;
        const width = canvas.clientWidth || 850;

        canvas.width = width * ratio;
        canvas.height = height * ratio;

        const ctx = canvas.getContext('2d');
        ctx.scale(ratio, ratio);

        return { ctx, width, height };
    }

    function distance(first, second) {
        return Math.hypot(first.x - second.x, first.y - second.y);
    }

    function format(value) {
        return Number(value).toLocaleString('ru-RU', {
            maximumFractionDigits: 3,
            minimumFractionDigits: 3
        });
    }

    function statusText(status) {
        const labels = {
            queued: 'в очереди',
            running: 'выполняется',
            completed: 'готово',
            cancelled: 'остановлено',
            failed: 'ошибка'
        };

        return labels[status] || status;
    }

    function escapeHtml(value) {
        const node = document.createElement('div');
        node.textContent = String(value);
        return node.innerHTML;
    }

    generateCities();
})();
