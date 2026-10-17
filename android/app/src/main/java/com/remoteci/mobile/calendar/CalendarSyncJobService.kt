package com.remoteci.mobile.calendar

import android.app.job.JobParameters
import android.app.job.JobService
import com.remoteci.mobile.data.ConnectionManager
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch

class CalendarSyncJobService : JobService() {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main)
    private var job: Job? = null

    override fun onStartJob(params: JobParameters): Boolean {
        ConnectionManager.initialize(this)
        job = scope.launch {
            val success = CalendarSync.syncIfEnabled(applicationContext)
            jobFinished(params, !success)
        }
        return true
    }

    override fun onStopJob(params: JobParameters): Boolean {
        job?.cancel()
        return true
    }

    override fun onDestroy() {
        scope.cancel()
        super.onDestroy()
    }
}
