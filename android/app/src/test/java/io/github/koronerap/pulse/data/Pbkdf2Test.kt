package io.github.koronerap.pulse.data

import org.junit.Assert.assertArrayEquals
import org.junit.Test
import javax.crypto.SecretKeyFactory
import javax.crypto.spec.PBEKeySpec
import kotlin.random.Random

/** Hızlı gerçekleme Java'nın standart PBKDF2'siyle aynı sonucu vermeli. */
class Pbkdf2Test {
    private fun standard(password: String, salt: ByteArray, iterations: Int): ByteArray =
        SecretKeyFactory.getInstance("PBKDF2WithHmacSHA256")
            .generateSecret(PBEKeySpec(password.toCharArray(), salt, iterations, 256)).encoded

    @Test
    fun matchesTheStandardImplementation() {
        val random = Random(42)

        for (iterations in listOf(1, 2, 3, 1000, 4097)) {
            val password = (1..8).joinToString("") { random.nextInt(10).toString() }
            val salt = random.nextBytes(16)

            assertArrayEquals("$iterations turda", standard(password, salt, iterations), Pbkdf2.sha256(password.toByteArray(), salt, iterations))
        }
    }

    /** RFC 7914'teki PBKDF2-HMAC-SHA256 örneği ("passwd", "salt", 1 tur), ilk 32 bayt. */
    @Test
    fun matchesThePublishedTestVector() {
        val expected = "55ac046e56e3089fec1691c22544b605f94185216dde0465e68b9d57c20dacbc"
            .chunked(2).map { it.toInt(16).toByte() }.toByteArray()

        assertArrayEquals(expected, Pbkdf2.sha256("passwd".toByteArray(), "salt".toByteArray(), 1))
    }
}
