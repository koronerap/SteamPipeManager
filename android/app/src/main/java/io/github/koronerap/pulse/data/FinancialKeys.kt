package io.github.koronerap.pulse.data

import android.content.SharedPreferences
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import java.security.GeneralSecurityException
import java.security.KeyStore
import java.util.Base64
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

/** Gizli değeri şifreleyip açan kutu; testlerde düz bir sahtesiyle değiştiriliyor. */
interface SecretBox {
    fun seal(plain: String): String

    /** Açılamazsa (anahtar silinmiş, veri bozuk) null. */
    fun open(sealed: String): String?
}

/**
 * Android Keystore'daki AES anahtarıyla şifreleme. Anahtar cihazdan çıkarılamıyor;
 * yedeklerle başka cihaza taşınan veri orada açılamıyor (uygulama yedeklemeyi de kapatıyor).
 */
class KeystoreBox(private val alias: String = "pulse-financial-keys") : SecretBox {
    private fun key(): SecretKey {
        val store = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        (store.getKey(alias, null) as? SecretKey)?.let { return it }

        val generator = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore")
        generator.init(
            KeyGenParameterSpec.Builder(alias, KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT)
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                .setKeySize(256)
                .build(),
        )
        return generator.generateKey()
    }

    override fun seal(plain: String): String {
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.ENCRYPT_MODE, key())
        return Base64.getEncoder().encodeToString(cipher.iv + cipher.doFinal(plain.toByteArray()))
    }

    override fun open(sealed: String): String? = try {
        val bytes = Base64.getDecoder().decode(sealed)
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.DECRYPT_MODE, key(), GCMParameterSpec(128, bytes, 0, 12))
        cipher.doFinal(bytes, 12, bytes.size - 12).decodeToString()
    } catch (_: GeneralSecurityException) {
        null
    } catch (_: IllegalArgumentException) {
        null
    }
}

/** Kayıtlı bir anahtar: kimliği ve ekranda gösterilen son dört hanesi. */
data class StoredKey(val id: String, val hint: String)

/**
 * Steam finans anahtarları, kimliklerine (parmak izi) göre, şifreli. Anahtar bir Steamworks
 * ortağının (şirketin) bütün verisini görüyor; ekranda yalnızca son dört hanesi görünüyor.
 */
class FinancialKeys(private val prefs: SharedPreferences, private val box: SecretBox) {
    /** Açılmış anahtarlar; her okumada Keystore'a gidilmesin (her oyun için, her çizimde okunuyor). */
    private val opened = java.util.concurrent.ConcurrentHashMap<String, String>()

    fun all(): List<StoredKey> = prefs.all.keys.filter { it.startsWith(PREFIX) }.mapNotNull { name ->
        val id = name.removePrefix(PREFIX)
        read(id)?.let { StoredKey(id, it.takeLast(4)) }
    }

    fun read(id: String): String? =
        opened[id] ?: prefs.getString(PREFIX + id, null)?.let(box::open)?.also { opened[id] = it }

    /** Kaydeder ve kimliğini döndürür; aynı anahtar ikinci kez eklenirse aynı kimlik. */
    fun add(key: String): String {
        val trimmed = key.trim()
        val id = FinancialAccount.fingerprint(trimmed)
        prefs.edit().putString(PREFIX + id, box.seal(trimmed)).apply()
        opened[id] = trimmed
        return id
    }

    fun remove(id: String) {
        prefs.edit().remove(PREFIX + id).apply()
        opened.remove(id)
    }

    private companion object {
        const val PREFIX = "key:"
    }
}
