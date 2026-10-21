package io.github.koronerap.pulse.data

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class PairingTest {
    private val data = PairingData(
        games = listOf(PairingGame(730, "Counter-Strike 2", 0), PairingGame(570, "Dota 2"), PairingGame(4000, "Ğüşİöç Oyun", 1)),
        keys = listOf("KEY-A", "KEY-B"),
        currency = "TRY",
    )

    private val salt = ByteArray(16) { it.toByte() }
    private val nonce = ByteArray(12) { (100 + it).toByte() }

    @Test
    fun whatTheDesktopSealsOpensWithTheCode() {
        val text = Pairing.encode(data, "1234 5678", salt, nonce, iterations = 1000)

        assertTrue(Pairing.looksLikePairing(text))
        assertEquals(data, Pairing.decode(text, "12345678", iterations = 1000))
    }

    /**
     * Masaüstünün (C#, PhonePairing.Seal) aynı girdilerle ürettiği metin: iki uygulama aynı
     * biçimi konuşuyor. Tersi masaüstü testlerinde (PhonePairingTests).
     */
    @Test
    fun whatTheRealDesktopAppSealedOpensHere() {
        assertEquals(data, Pairing.decode(DESKTOP_VECTOR, "12345678", iterations = 1000))

        // Aynı girdilerle bayt bayt aynı metin: masaüstü testindeki vektör de bu.
        assertEquals(DESKTOP_VECTOR, Pairing.encode(data, "12345678", salt, nonce, iterations = 1000))
    }

    @Test
    fun aWrongCodeOrADamagedCodeOpensNothing() {
        val text = Pairing.encode(data, "12345678", salt, nonce, iterations = 1000)

        assertNull(Pairing.decode(text, "12345679", iterations = 1000))
        assertNull(Pairing.decode(text.dropLast(4) + "AAAA", "12345678", iterations = 1000))
        assertNull(Pairing.decode("SPMPULSE1.not-base64!", "12345678", iterations = 1000))
        assertNull(Pairing.decode("https://example.com", "12345678", iterations = 1000))
        assertFalse(Pairing.looksLikePairing("https://example.com"))
    }

    private companion object {
        /** Tuz 0..15, nonce 100..111, kod 12345678, 1000 tur; üreten: masaüstü (C#). */
        const val DESKTOP_VECTOR =
            "SPMPULSE1.AAECAwQFBgcICQoLDA0OD2RlZmdoaWprbG1ubzpYCZujUSzwQvVNWOjxpbdjK1R_8NYCpfVjs7AYp20l261CM6J3V79e-DGEviQNQpqNp6T9jYy1rclfDm2niCscU0a_LTNNb6xA4lUaXUB2ODaL3AQVCkAnItzEkh9zRLoOI94VSWAootEMouUdBFamTzwpgNU9jj_03m8WzFyhAjdaVIWqVtA2wXDPaJcFwueBgWOUw2QJIdXL"
    }
}
