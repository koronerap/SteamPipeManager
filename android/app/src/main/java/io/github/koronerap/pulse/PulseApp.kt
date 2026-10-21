package io.github.koronerap.pulse

import android.app.Application
import io.github.koronerap.pulse.work.RefreshWorker

class PulseApp : Application() {
    override fun onCreate() {
        super.onCreate()

        // Düzenli yenileme bir kez kuruluyor; aralık ayarlardan değişince yeniden.
        RefreshWorker.schedule(this, Pulse.get(this).preferences.refreshMinutes)
    }
}
