import React, { useLayoutEffect, useRef, useState } from 'react';
import { ArrowFatUpIcon, BackspaceIcon } from '@phosphor-icons/react';

const layouts = {
  ru: ['йцукенгшщзхъ', 'фывапролджэ', 'ячсмитьбюё'],
  en: ['qwertyuiop', 'asdfghjkl', 'zxcvbnm']
};

export default function IntakeKeyboard({ inputRef, value, onChange, numeric = false, disabled = false }) {
  const [language, setLanguage] = useState('ru');
  const [uppercase, setUppercase] = useState(!value || /[\s'-]$/.test(value));
  const caret = useRef(null);
  useLayoutEffect(() => {
    const input = inputRef.current;
    if (caret.current !== null && input) {
      if (!numeric) input.setSelectionRange(caret.current, caret.current);
      caret.current = null;
    }
    if (!numeric) {
      const before = value.slice(0, input?.selectionStart ?? value.length);
      setUppercase(!before || /[\s'-]$/.test(before));
    }
  }, [value, inputRef, numeric]);

  function edit(text = '', action = 'insert') {
    if (disabled) return;
    const input = inputRef.current;
    let start = numeric ? value.length : input?.selectionStart ?? value.length;
    const end = numeric ? value.length : input?.selectionEnd ?? value.length;
    let next;
    let position;
    if (action === 'clear') {
      next = '';
      position = 0;
    } else {
      if (action === 'backspace' && start === end) {
        start -= Array.from(value.slice(0, start)).at(-1)?.length || 0;
      }
      const inserted = text.slice(0, Math.max(0, (numeric ? 6 : 120) - value.length + end - start));
      position = start + inserted.length;
      next = value.slice(0, start) + inserted + value.slice(end);
    }
    input?.focus({ preventScroll: true });
    if (next === value) {
      caret.current = null;
      if (!numeric) input?.setSelectionRange(position, position);
    } else {
      caret.current = position;
      onChange(next);
    }
  }

  function key(label, content, onClick, className = '') {
    return <button key={label} type="button" className={`intake-key ${className}`} aria-label={label} disabled={disabled}
      onPointerDown={event => event.preventDefault()} onClick={onClick}>{content}</button>;
  }
  const backspace = () => key('Удалить символ', <BackspaceIcon size={24} aria-hidden="true" />, () => edit('', 'backspace'), 'intake-key-control');
  const clear = () => key(numeric ? 'Очистить количество' : 'Очистить имя', 'Очистить', () => edit('', 'clear'), 'intake-key-control');

  return <div className={`intake-keyboard ${numeric ? 'numeric' : ''}`} role="group" aria-label={numeric ? 'Клавиатура количества' : 'Экранная клавиатура имени'}>
    {numeric ? <div className="intake-number-keys">
      {Array.from('123456789', digit => key(`Цифра ${digit}`, digit, () => edit(digit)))}
      {clear()}{key('Цифра 0', '0', () => edit('0'))}{backspace()}
    </div> : <>
      {layouts[language].map((letters, index) => <div key={index} className="intake-key-row">
        {index === 2 && <button type="button" className={`intake-key intake-key-control ${uppercase ? 'active' : ''}`} aria-label="Заглавные буквы" aria-pressed={uppercase}
          disabled={disabled} onPointerDown={event => event.preventDefault()} onClick={() => setUppercase(current => !current)}><ArrowFatUpIcon size={23} aria-hidden="true" /></button>}
        {Array.from(uppercase ? letters.toLocaleUpperCase(language) : letters, letter => key(`Буква ${letter}`, letter, () => edit(letter)))}
        {index === 2 && backspace()}
      </div>)}
      <div className="intake-key-row intake-keyboard-tools">
        {key(language === 'ru' ? 'Английская раскладка' : 'Русская раскладка', language === 'ru' ? 'RU → EN' : 'EN → RU', () => setLanguage(current => current === 'ru' ? 'en' : 'ru'), 'intake-key-control')}
        {key('Дефис', '−', () => edit('-'))}
        {key('Пробел', 'Пробел', () => edit(' '), 'intake-key-space')}
        {key('Апостроф', '’', () => edit("'"))}
        {clear()}
      </div>
    </>}
  </div>;
}
