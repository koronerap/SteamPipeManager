package io.github.koronerap.pulse.data

import kotlinx.serialization.Serializable
import java.io.File

/**
 * Takip edilen bir oyun. [keyId]: finans anahtarının kimliği (SHA-256 parmak izi); yoksa
 * yalnızca oyuncu sayısı. [showInWidget]: ana ekran widget'ında görünsün mü.
 */
@Serializable
data class TrackedGame(val appId: Long, val title: String, val keyId: String? = null, val showInWidget: Boolean = true)

@Serializable
private data class GameList(val games: List<TrackedGame> = emptyList())

/** Oyun listesi (`games.json`). Anahtarlar burada değil, şifreli depoda. */
class GameRepository(file: File) {
    private val store = JsonFile(file, GameList.serializer(), ::GameList)

    fun load(): List<TrackedGame> = store.load().games

    fun save(games: List<TrackedGame>) = store.save(GameList(games))

    /** Aynı App ID varsa yerine yazılıyor (ad ve anahtar yeni gelenden), yoksa sona ekleniyor. */
    fun merge(incoming: List<TrackedGame>): List<TrackedGame> {
        val games = load().toMutableList()

        for (game in incoming) {
            val index = games.indexOfFirst { it.appId == game.appId }

            if (index >= 0) {
                games[index] = games[index].copy(title = game.title, keyId = game.keyId ?: games[index].keyId)
            } else {
                games.add(game)
            }
        }

        save(games)
        return games
    }
}
