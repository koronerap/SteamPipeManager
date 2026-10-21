package io.github.koronerap.pulse.data

import kotlinx.serialization.Serializable
import kotlinx.serialization.SerializationException
import kotlinx.serialization.json.Json
import java.io.ByteArrayOutputStream
import java.security.GeneralSecurityException
import java.util.Base64
import java.util.zip.DataFormatException
import java.util.zip.Deflater
import java.util.zip.DeflaterOutputStream
import java.util.zip.Inflater
import javax.crypto.Cipher
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.SecretKeySpec

/** QR'daki bir oyun: App ID, ad ve (varsa) anahtar listesindeki sırası. */
@Serializable
data class PairingGame(val id: Long, val title: String, val key: Int? = null)

/** Masaüstünün "Telefona aktar" verisi. */
@Serializable
data class PairingData(val v: Int = 1, val games: List<PairingGame>, val keys: List<String> = emptyList(), val currency: String? = null)

/**
 * Masaüstünden QR ile aktarım. QR finans anahtarlarını içeriyor; masaüstü ekranda ayrıca
 * 8 haneli bir kod gösteriyor ve veri o kodla şifreli: QR'ın fotoğrafı tek başına işe
 * yaramıyor.
 *
 * Biçim (masaüstündeki PhonePairing ile aynı): `SPMPULSE1.` + base64url(tuz[16] |
 * nonce[12] | şifreli + etiket). Anahtar PBKDF2-HMAC-SHA256(kod, tuz, 600.000 tur, 32 bayt);
 * AES-256-GCM, ek veri "SPMPULSE1"; içerik ham DEFLATE ile sıkıştırılmış JSON.
 */
object Pairing {
    const val PREFIX = "SPMPULSE1."
    const val ITERATIONS = 600_000

    private const val SALT_SIZE = 16
    private const val NONCE_SIZE = 12
    private val AAD = "SPMPULSE1".toByteArray()
    private val json = Json { ignoreUnknownKeys = true; encodeDefaults = true; explicitNulls = false }

    fun looksLikePairing(text: String): Boolean = text.trim().startsWith(PREFIX)

    /** Kodun rakamları; boşluk ve tireler yok sayılıyor. */
    fun normalizeCode(code: String): String = code.filter(Char::isDigit)

    /** Kod yanlışsa ya da veri bozuksa null. Uzun sürüyor (PBKDF2); arka planda çağrılmalı. */
    fun decode(text: String, code: String, iterations: Int = ITERATIONS): PairingData? {
        val payload = text.trim()
        if (!payload.startsWith(PREFIX)) return null

        return try {
            val bytes = Base64.getUrlDecoder().decode(payload.substring(PREFIX.length))
            if (bytes.size <= SALT_SIZE + NONCE_SIZE + 16) return null

            val salt = bytes.copyOfRange(0, SALT_SIZE)
            val nonce = bytes.copyOfRange(SALT_SIZE, SALT_SIZE + NONCE_SIZE)
            val sealed = bytes.copyOfRange(SALT_SIZE + NONCE_SIZE, bytes.size)

            val cipher = Cipher.getInstance("AES/GCM/NoPadding")
            cipher.init(Cipher.DECRYPT_MODE, key(normalizeCode(code), salt, iterations), GCMParameterSpec(128, nonce))
            cipher.updateAAD(AAD)

            json.decodeFromString(PairingData.serializer(), inflate(cipher.doFinal(sealed)).decodeToString())
        } catch (_: GeneralSecurityException) {
            null
        } catch (_: IllegalArgumentException) {
            null
        } catch (_: SerializationException) {
            null
        } catch (_: DataFormatException) {
            null
        }
    }

    /** Testlerde masaüstünün ürettiğinin karşılığını üretmek için. */
    fun encode(data: PairingData, code: String, salt: ByteArray, nonce: ByteArray, iterations: Int = ITERATIONS): String {
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.ENCRYPT_MODE, key(normalizeCode(code), salt, iterations), GCMParameterSpec(128, nonce))
        cipher.updateAAD(AAD)

        val sealed = cipher.doFinal(deflate(json.encodeToString(PairingData.serializer(), data).toByteArray()))
        return PREFIX + Base64.getUrlEncoder().withoutPadding().encodeToString(salt + nonce + sealed)
    }

    private fun key(code: String, salt: ByteArray, iterations: Int): SecretKeySpec =
        SecretKeySpec(Pbkdf2.sha256(code.toByteArray(), salt, iterations), "AES")

    private fun deflate(data: ByteArray): ByteArray {
        val output = ByteArrayOutputStream()
        DeflaterOutputStream(output, Deflater(Deflater.BEST_COMPRESSION, true)).use { it.write(data) }
        return output.toByteArray()
    }

    private fun inflate(data: ByteArray): ByteArray {
        val inflater = Inflater(true)
        inflater.setInput(data)
        val output = ByteArrayOutputStream()
        val buffer = ByteArray(4096)

        while (!inflater.finished()) {
            val count = inflater.inflate(buffer)

            if (count == 0 && (inflater.needsInput() || inflater.needsDictionary())) {
                break
            }

            output.write(buffer, 0, count)
        }

        inflater.end()
        return output.toByteArray()
    }
}
