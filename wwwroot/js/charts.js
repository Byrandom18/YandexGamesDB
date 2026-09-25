window.ygCharts = {
    _charts: {},
    bar(id, labels, values, color, horizontal) {
        const el = document.getElementById(id);
        if (!el) return;
        if (typeof Chart === 'undefined') {
            setTimeout(() => this.bar(id, labels, values, color, horizontal), 60);
            return;
        }
        if (this._charts[id]) {
            this._charts[id].destroy();
        }
        const ctx = el.getContext('2d');
        this._charts[id] = new Chart(ctx, {
            type: 'bar',
            data: {
                labels,
                datasets: [{
                    data: values,
                    backgroundColor: color || '#ffd100',
                    borderRadius: 8,
                    maxBarThickness: 28
                }]
            },
            options: {
                indexAxis: horizontal ? 'y' : 'x',
                responsive: true,
                maintainAspectRatio: false,
                plugins: {
                    legend: { display: false },
                    tooltip: {
                        backgroundColor: '#141824',
                        titleColor: '#eef1f7',
                        bodyColor: '#eef1f7',
                        borderColor: 'rgba(255,255,255,.08)',
                        borderWidth: 1
                    }
                },
                scales: {
                    x: {
                        ticks: { color: '#8b93a7', maxRotation: 45, minRotation: 0, autoSkip: false },
                        grid: { color: 'rgba(255,255,255,.06)' }
                    },
                    y: {
                        ticks: { color: '#8b93a7', autoSkip: false },
                        grid: { color: 'rgba(255,255,255,.06)' }
                    }
                }
            }
        });
    }
};
