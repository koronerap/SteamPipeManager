package io.github.koronerap.pulse.work

import android.content.Context
import androidx.work.Constraints
import androidx.work.CoroutineWorker
import androidx.work.ExistingPeriodicWorkPolicy
import androidx.work.ExistingWorkPolicy
import androidx.work.NetworkType
import androidx.work.OneTimeWorkRequestBuilder
import androidx.work.PeriodicWorkRequestBuilder
import androidx.work.WorkManager
import androidx.work.WorkerParameters
import io.github.koronerap.pulse.Pulse
import io.github.koronerap.pulse.data.RefreshResult
import java.util.concurrent.TimeUnit

/**
 * Arka plan yenilemesi: oyuncu sayıları her turda, wishlist ve satışlar üç saatte bir
 * (servis kendi aralığını tutuyor). Geçmiş bir turda bitmezse bir dakika sonra devam.
 */
class RefreshWorker(context: Context, params: WorkerParameters) : CoroutineWorker(context, params) {
    override suspend fun doWork(): Result {
        val result = Pulse.get(applicationContext).refresh(force = inputData.getBoolean(FORCE, false))

        if (result == RefreshResult.MorePending) {
            continueSoon(applicationContext)
        }

        return Result.success()
    }

    companion object {
        private const val PERIODIC = "pulse-refresh"
        private const val NOW = "pulse-refresh-now"
        private const val BACKFILL = "pulse-backfill"
        private const val FORCE = "force"

        private val network = Constraints.Builder().setRequiredNetworkType(NetworkType.CONNECTED).build()

        /** Düzenli yenileme; aralık değişince [replace] ile yeniden kuruluyor. */
        fun schedule(context: Context, minutes: Int, replace: Boolean = false) {
            val request = PeriodicWorkRequestBuilder<RefreshWorker>(minutes.coerceAtLeast(15).toLong(), TimeUnit.MINUTES)
                .setConstraints(network)
                .build()

            WorkManager.getInstance(context).enqueueUniquePeriodicWork(
                PERIODIC,
                if (replace) ExistingPeriodicWorkPolicy.UPDATE else ExistingPeriodicWorkPolicy.KEEP,
                request,
            )
        }

        /** Widget'taki yenile düğmesi: hemen, aralıklar beklenmeden. */
        fun runNow(context: Context) {
            val request = OneTimeWorkRequestBuilder<RefreshWorker>()
                .setConstraints(network)
                .setInputData(androidx.work.workDataOf(FORCE to true))
                .build()

            WorkManager.getInstance(context).enqueueUniqueWork(NOW, ExistingWorkPolicy.REPLACE, request)
        }

        private fun continueSoon(context: Context) {
            val request = OneTimeWorkRequestBuilder<RefreshWorker>()
                .setConstraints(network)
                .setInitialDelay(1, TimeUnit.MINUTES)
                .build()

            WorkManager.getInstance(context).enqueueUniqueWork(BACKFILL, ExistingWorkPolicy.REPLACE, request)
        }
    }
}
